using System.Text;

namespace LR.Core.Wrapper;

/// <summary>
/// Quotes argument lists into a single Windows command-line string using the same
/// backslash/quote escaping rules the .NET runtime applies internally for
/// <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/> (and that the standard
/// MSVC/CommandLineToArgvW C-runtime argv parser expects on the receiving end). Needed
/// wherever a flattened single-line command is required instead of ArgumentList — e.g. a
/// generated .bat file line, or a human-readable "start command" preview.
/// </summary>
public static class WindowsCommandLine
{
    public static string Join(IEnumerable<string> arguments)
    {
        var sb = new StringBuilder();
        foreach (var argument in arguments)
        {
            if (sb.Length != 0)
                sb.Append(' ');
            AppendArgument(sb, argument);
        }
        return sb.ToString();
    }

    private static void AppendArgument(StringBuilder sb, string argument)
    {
        if (argument.Length != 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            sb.Append(argument);
            return;
        }

        sb.Append('"');
        int idx = 0;
        while (idx < argument.Length)
        {
            char c = argument[idx++];
            if (c == '\\')
            {
                int numBackslash = 1;
                while (idx < argument.Length && argument[idx] == '\\')
                {
                    idx++;
                    numBackslash++;
                }

                if (idx == argument.Length)
                {
                    // Backslashes at the end of the argument: double them, since the closing
                    // quote we're about to append would otherwise escape the last one.
                    sb.Append('\\', numBackslash * 2);
                }
                else if (argument[idx] == '"')
                {
                    // Backslashes immediately before a literal quote: double them, then escape the quote.
                    sb.Append('\\', numBackslash * 2 + 1);
                    sb.Append('"');
                    idx++;
                }
                else
                {
                    sb.Append('\\', numBackslash);
                }
            }
            else if (c == '"')
            {
                sb.Append('\\').Append('"');
            }
            else
            {
                sb.Append(c);
            }
        }
        sb.Append('"');
    }
}
