// SYCL keep-alive for Intel GPUs on Windows.
//
// With no monitor attached, the Intel driver releases the GPU's VRAM contents after roughly a
// minute of inactivity, so an idle llama.cpp/SYCL server loses its loaded model. Submitting a
// no-op kernel every few seconds keeps the device active. One process keeps every selected
// card alive (all Intel GPUs by default), so a machine never needs more than one instance.
//
// Background: https://www.sockless.tech/articles/keeping-intel-gpu-vram-alive-a-sycl-keepalive-solution-for-local-ai-on-windows/
//
// Usage:
//   sycl-keepalive [--device <selector>]... [--interval <seconds>]
//   sycl-keepalive --list
//
//   --device    Keep only matching GPUs alive (repeatable). A selector is a PCI address
//               ("0000:03:00.0" or "03:00.0"), an index from --list, or a case-insensitive
//               part of the device name. Default: every GPU.
//   --interval  Seconds between kernel submissions (1-50, default 10).
//   --list      Print the GPUs this process can see and exit.
//
// Exit codes: 0 = --list/--help, 1 = runtime error, 2 = bad arguments, 3 = no matching GPU.
//
// Build (from a oneAPI prompt, or after "C:\Program Files (x86)\Intel\oneAPI\setvars.bat" intel64):
//   icx -fsycl -O2 keepalive.cpp -o sycl-keepalive.exe
//
// The binary links against the oneAPI SYCL runtime (sycl9.dll), so it has to run with the
// oneAPI environment loaded. The router does that through its configurable setup command.

#include <sycl/sycl.hpp>

#include <algorithm>
#include <cctype>
#include <chrono>
#include <cstdlib>
#include <iostream>
#include <string>
#include <thread>
#include <vector>

namespace {

struct Gpu {
    sycl::device device;
    std::string name;
    std::string pci;  // "DDDD:BB:DD.F", empty when the backend can't report it
};

std::string lower(std::string s) {
    std::transform(s.begin(), s.end(), s.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    return s;
}

// GPUs show up once per backend (Level Zero and OpenCL); prefer Level Zero, which is what
// llama.cpp's SYCL backend uses, and fall back to whatever GPUs exist without it.
std::vector<Gpu> discover() {
    auto all = sycl::device::get_devices(sycl::info::device_type::gpu);
    bool any_level_zero = std::any_of(all.begin(), all.end(),
        [](const sycl::device& d) { return d.get_backend() == sycl::backend::ext_oneapi_level_zero; });

    std::vector<Gpu> gpus;
    for (auto& d : all) {
        if (any_level_zero && d.get_backend() != sycl::backend::ext_oneapi_level_zero)
            continue;
        Gpu gpu{d, d.get_info<sycl::info::device::name>(), {}};
        if (d.has(sycl::aspect::ext_intel_pci_address)) {
            try { gpu.pci = lower(d.get_info<sycl::ext::intel::info::device::pci_address>()); }
            catch (const sycl::exception&) {}
        }
        gpus.push_back(std::move(gpu));
    }
    return gpus;
}

bool matches(const Gpu& gpu, size_t index, const std::string& selector) {
    auto sel = lower(selector);
    if (!gpu.pci.empty()) {
        // Accept the address with or without the leading PCI domain ("0000:").
        if (gpu.pci == sel)
            return true;
        auto colon = gpu.pci.find(':');
        if (colon != std::string::npos && gpu.pci.substr(colon + 1) == sel)
            return true;
    }
    if (!sel.empty() && std::all_of(sel.begin(), sel.end(), [](unsigned char c) { return std::isdigit(c); }))
        return std::stoul(sel) == index;
    return lower(gpu.name).find(sel) != std::string::npos;
}

void print_usage() {
    std::cout << "Usage: sycl-keepalive [--device <pci-address|index|name>]... [--interval <seconds>]\n"
                 "       sycl-keepalive --list\n";
}

}  // namespace

int main(int argc, char** argv) {
    std::vector<std::string> selectors;
    int interval = 10;
    bool list = false;

    for (int i = 1; i < argc; ++i) {
        std::string arg = argv[i];
        if ((arg == "--device" || arg == "-d") && i + 1 < argc) {
            selectors.emplace_back(argv[++i]);
        } else if (arg == "--interval" && i + 1 < argc) {
            interval = std::atoi(argv[++i]);
            if (interval < 1 || interval > 50) {
                std::cerr << "--interval must be between 1 and 50 seconds (the driver drops VRAM after ~60s idle)." << std::endl;
                return 2;
            }
        } else if (arg == "--list") {
            list = true;
        } else if (arg == "--help" || arg == "-h" || arg == "/?") {
            print_usage();
            return 0;
        } else {
            std::cerr << "Unknown or incomplete argument: " << arg << std::endl;
            print_usage();
            return 2;
        }
    }

    try {
        auto gpus = discover();

        if (list) {
            for (size_t i = 0; i < gpus.size(); ++i)
                std::cout << i << "  " << (gpus[i].pci.empty() ? "(no PCI address)" : gpus[i].pci) << "  " << gpus[i].name << '\n';
            if (gpus.empty())
                std::cout << "No GPUs visible to SYCL." << '\n';
            return 0;
        }

        std::vector<sycl::queue> queues;
        for (size_t i = 0; i < gpus.size(); ++i) {
            bool selected = selectors.empty() ||
                std::any_of(selectors.begin(), selectors.end(), [&](const std::string& s) { return matches(gpus[i], i, s); });
            if (!selected)
                continue;
            queues.emplace_back(gpus[i].device);
            std::cout << "Running SYCL keep-alive on: " << gpus[i].name;
            if (!gpus[i].pci.empty())
                std::cout << " [" << gpus[i].pci << "]";
            std::cout << std::endl;
        }

        if (queues.empty()) {
            std::cerr << (gpus.empty() ? "No GPUs visible to SYCL." : "No GPU matched the --device selector(s); run with --list to see them.") << std::endl;
            return 3;
        }

        std::cout << "Submitting a no-op kernel every " << interval << "s to " << queues.size() << " device(s)." << std::endl;

        for (;;) {
            for (auto& q : queues) {
                try {
                    q.submit([](sycl::handler& cgh) { cgh.single_task([]() {}); }).wait();
                } catch (const sycl::exception& e) {
                    // One card failing (e.g. reset or disabled) shouldn't stop the others.
                    std::cerr << "Kernel submit failed on " << q.get_device().get_info<sycl::info::device::name>()
                              << ": " << e.what() << std::endl;
                }
            }
            std::this_thread::sleep_for(std::chrono::seconds(interval));
        }
    } catch (const std::exception& e) {
        std::cerr << "SYCL keep-alive failed: " << e.what() << std::endl;
        return 1;
    }
}
