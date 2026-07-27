# LLM Router

A lightweight .NET-based routing layer for managing and load-balancing across multiple local LLM inference servers.

## Overview

LLM Router provides a unified interface to route requests across heterogeneous backend inference engines (CPU, Vulkan, SYCL, etc.) running on different machines or GPUs. It features:

- **Multi-backend support** — Route to Llama.cpp backends with CPU, Vulkan, or SYCL acceleration via a pluggable provider model.
- **Health monitoring** — Background health checks to detect and handle server failures automatically.
- **Model presets** — Manage routing rules and model configurations through a preset system.
- **Razor Pages UI** — Web dashboard for managing servers, presets, and viewing status.

## Architecture

```
┌───────────────┐
│   Razor Pages  │  ← Web UI
└───────┬───────┘
        │
┌───────▼───────┐     ┌──────────────┐
│  Routing Engine│────▶│ Server Manager│  ← Health monitoring + server registry
└───────┬───────┘     └──────────────┘
        │
┌───────▼───────┐     ┌──────────────┐
│ Preset Manager │     │  Providers   │  ← Backend provider abstractions
└───────────────┘     └──────────────┘
```

### Projects

| Project | Description |
|---|---|
| **LR.Application** | Web application (Razor Pages) and health monitoring service |
| **LR.Core** | Core interfaces, models, and services (routing engine, server manager, preset manager) |
| **LR.Providers** | Backend provider implementations (Llama.cpp with CPU/Vulkan/SYCL backends) |

## Getting Started

### Prerequisites

- .NET 10.0 SDK or later

### Running the Application

```bash
dotnet run --project LR.Application
```

The application will start and serve a Razor Pages dashboard.

## Configuration

Edit `LR.Application/appsettings.json` to configure servers, presets, and backend provider settings.

## License

This project is licensed under the MIT License — see the [LICENSE](LICENSE) file for details.

