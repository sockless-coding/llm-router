using System.Text;
using System.Text.RegularExpressions;

using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Core.Services;

/// <summary>
/// Extracts free (custom) variables from a raw Jinja2 chat-template string via a hand-rolled
/// lexer and scope-tracked identifier walk — NOT a spec-compliant Jinja2/minja parser. It builds
/// no AST and never evaluates the template; it only tokenizes tag boundaries and expression
/// tokens well enough to tell which identifiers are "free" (read from the render context) versus
/// locally bound (for/set/macro/with) or part of llama.cpp/minja's standard context.
///
/// Documented heuristic limitations (acceptable for surfacing real-world chat-template kwargs,
/// not for arbitrary Jinja2 correctness):
/// - `{% raw %}...{% endraw %}` bodies are treated as opaque text and never tokenized.
/// - `{% else %}` on a `for` loop does not pop the loop scope early — loop variables stay
///   "bound" through the else-branch, a rare enough construct in chat templates to accept.
/// - `call(args) macroname(...)` binds `args` into the current scope rather than a nested one.
/// - A bare `func(...)` call in expression position is reported as a free variable when `func`
///   isn't a known filter/builtin/bound name — there's no reliable way to distinguish a real
///   custom callable from a typo without a real Jinja environment.
/// - No true block matching: the scope stack is purely sequential and assumes a well-formed,
///   well-nested template (as GGUF-sourced templates from working models should be). An unpaired
///   `end*` is a no-op rather than an error.
/// - Literal-value collection (== / != / "in [...]" / default(...)) only looks at a value's
///   immediate syntactic neighbors — values gated behind more complex expressions won't surface.
/// </summary>
public class ChatTemplateVariableExtractor : IChatTemplateVariableExtractor
{
    // Fixed render-context data variables llama.cpp/minja chat templates commonly receive,
    // assembled from public llama.cpp/minja source. Best-effort, not guaranteed complete —
    // under-filtering a standard variable as "custom" is preferable to hardcode-missing a
    // real custom kwarg, so this list should be revisited if false positives are reported.
    private static readonly HashSet<string> StandardContextVariables = new(StringComparer.Ordinal)
    {
        "messages", "message", "tools", "tool", "tool_call", "tool_calls", "tool_calls_section",
        "content", "role", "name", "arguments", "system_message",
        "bos_token", "eos_token", "unk_token", "pad_token", "cls_token", "sep_token", "mask_token",
        "add_generation_prompt", "add_bos_token", "add_eos_token",
        "strftime_now", "raise_exception", "date_string", "today",
        "tools_in_user_message", "documents", "citations", "builtin_tools", "custom_tools",
        "function", "functions", "id", "index", "type"
    };

    // Jinja/minja globals — bound everywhere, never part of the render context we care about.
    private static readonly HashSet<string> Builtins = new(StringComparer.Ordinal)
    {
        "range", "dict", "namespace", "lipsum", "cycler", "joiner", "super", "self",
        "loop", "varargs", "kwargs", "caller", "undefined"
    };

    // Keyword identifiers that act as operators, not variable references.
    private static readonly HashSet<string> KeywordOperators = new(StringComparer.Ordinal)
    {
        "and", "or", "not", "in", "is", "if", "else",
        "true", "false", "none", "True", "False", "None"
    };

    private static readonly Regex EndRawRegex = new(@"\{%-?\s*endraw\s*-?%\}", RegexOptions.Compiled);

    private enum SegmentKind { Output, Statement, Comment }
    private readonly record struct Segment(SegmentKind Kind, string Body);

    private enum TokenKind { Identifier, String, Number, Punct, Op }
    private readonly record struct Token(TokenKind Kind, string Text);

    private static readonly string[] MultiCharOperators = { "==", "!=", "<=", ">=", "//", "**" };

    public IReadOnlyList<ChatTemplateVariable> Extract(string? chatTemplate)
    {
        if (string.IsNullOrWhiteSpace(chatTemplate))
            return Array.Empty<ChatTemplateVariable>();

        try
        {
            var segments = Scan(chatTemplate);
            var results = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var scopeStack = new Stack<HashSet<string>>();
            scopeStack.Push(new HashSet<string>(StringComparer.Ordinal));
            // Macro names persist after {% endmacro %} (unlike for/set/with scopes) since a
            // macro remains callable for the rest of the template once declared.
            var macroNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (var segment in segments)
            {
                var tokens = Tokenize(segment.Body);
                if (tokens.Count == 0)
                    continue;

                if (segment.Kind == SegmentKind.Statement)
                    ProcessStatement(tokens, scopeStack, macroNames, results);
                else
                    WalkExpression(tokens, 0, tokens.Count, scopeStack, macroNames, results);
            }

            return results
                .Select(kv => new ChatTemplateVariable
                {
                    Name = kv.Key,
                    LiteralValues = kv.Value.Distinct(StringComparer.Ordinal).ToArray()
                })
                .OrderBy(v => v.Name, StringComparer.Ordinal)
                .ToArray();
        }
        catch
        {
            return Array.Empty<ChatTemplateVariable>();
        }
    }

    #region Stage 1 — tag scanner

    private static List<Segment> Scan(string template)
    {
        var segments = new List<Segment>();
        int i = 0;
        int n = template.Length;

        while (i < n)
        {
            int outputIdx = template.IndexOf("{{", i, StringComparison.Ordinal);
            int statementIdx = template.IndexOf("{%", i, StringComparison.Ordinal);
            int commentIdx = template.IndexOf("{#", i, StringComparison.Ordinal);

            int tagStart = int.MaxValue;
            var kind = SegmentKind.Output;
            if (outputIdx >= 0 && outputIdx < tagStart) { tagStart = outputIdx; kind = SegmentKind.Output; }
            if (statementIdx >= 0 && statementIdx < tagStart) { tagStart = statementIdx; kind = SegmentKind.Statement; }
            if (commentIdx >= 0 && commentIdx < tagStart) { tagStart = commentIdx; kind = SegmentKind.Comment; }

            if (tagStart == int.MaxValue)
                break; // remainder is plain text — nothing left to extract

            bool trimLeft = tagStart + 2 < n && template[tagStart + 2] == '-';
            int bodyStart = tagStart + 2 + (trimLeft ? 1 : 0);

            string closeDelim = kind switch
            {
                SegmentKind.Output => "}}",
                SegmentKind.Statement => "%}",
                _ => "#}"
            };

            var (body, nextIndex) = ScanTagBody(template, bodyStart, closeDelim, trackStrings: kind != SegmentKind.Comment);

            if (kind == SegmentKind.Statement && body.Trim() == "raw")
            {
                // Raw block: everything up to {% endraw %} is opaque literal text, never tokenized.
                var match = EndRawRegex.Match(template, nextIndex);
                i = match.Success ? match.Index + match.Length : n;
                continue;
            }

            if (kind != SegmentKind.Comment)
                segments.Add(new Segment(kind, body));

            i = nextIndex;
        }

        return segments;
    }

    private static (string Body, int NextIndex) ScanTagBody(string template, int bodyStart, string closeDelim, bool trackStrings)
    {
        int n = template.Length;
        char? quote = null;
        int j = bodyStart;

        while (j < n)
        {
            char c = template[j];

            if (trackStrings && quote.HasValue)
            {
                if (c == '\\' && j + 1 < n) { j += 2; continue; }
                if (c == quote.Value) { quote = null; j++; continue; }
                j++;
                continue;
            }

            if (trackStrings && (c == '\'' || c == '"'))
            {
                quote = c;
                j++;
                continue;
            }

            if (c == '-' && j + 2 < n && template[j + 1] == closeDelim[0] && template[j + 2] == closeDelim[1])
                return (template[bodyStart..j], j + 3);

            if (c == closeDelim[0] && j + 1 < n && template[j + 1] == closeDelim[1])
                return (template[bodyStart..j], j + 2);

            j++;
        }

        // EOF without a closing delimiter — malformed/truncated template; best-effort partial body.
        return (template[bodyStart..n], n);
    }

    #endregion

    #region Stage 2 — expression tokenizer

    private static List<Token> Tokenize(string body)
    {
        var tokens = new List<Token>();
        int i = 0;
        int n = body.Length;

        while (i < n)
        {
            char c = body[i];

            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < n && (char.IsLetterOrDigit(body[i]) || body[i] == '_')) i++;
                tokens.Add(new Token(TokenKind.Identifier, body[start..i]));
                continue;
            }

            if (char.IsDigit(c))
            {
                int start = i;
                while (i < n && char.IsDigit(body[i])) i++;
                if (i < n && body[i] == '.' && i + 1 < n && char.IsDigit(body[i + 1]))
                {
                    i++;
                    while (i < n && char.IsDigit(body[i])) i++;
                }
                tokens.Add(new Token(TokenKind.Number, body[start..i]));
                continue;
            }

            if (c is '\'' or '"')
            {
                char quote = c;
                i++;
                var sb = new StringBuilder();
                while (i < n && body[i] != quote)
                {
                    if (body[i] == '\\' && i + 1 < n)
                    {
                        sb.Append(body[i + 1]);
                        i += 2;
                    }
                    else
                    {
                        sb.Append(body[i]);
                        i++;
                    }
                }
                if (i < n) i++; // consume closing quote
                tokens.Add(new Token(TokenKind.String, sb.ToString()));
                continue;
            }

            bool matchedOp = false;
            foreach (var op in MultiCharOperators)
            {
                if (i + op.Length <= n && string.CompareOrdinal(body, i, op, 0, op.Length) == 0)
                {
                    tokens.Add(new Token(TokenKind.Op, op));
                    i += op.Length;
                    matchedOp = true;
                    break;
                }
            }
            if (matchedOp) continue;

            if (".,:(){}[]~".IndexOf(c) >= 0)
            {
                tokens.Add(new Token(TokenKind.Punct, c.ToString()));
                i++;
                continue;
            }

            if ("<>+-*/%|=".IndexOf(c) >= 0)
            {
                tokens.Add(new Token(TokenKind.Op, c.ToString()));
                i++;
                continue;
            }

            // Unrecognized character — skip rather than fail the whole scan.
            i++;
        }

        return tokens;
    }

    #endregion

    #region Stage 3 — scope-tracked free-variable walk

    private static void ProcessStatement(List<Token> tokens, Stack<HashSet<string>> scopeStack, HashSet<string> macroNames, Dictionary<string, List<string>> results)
    {
        if (tokens[0].Kind != TokenKind.Identifier)
        {
            WalkExpression(tokens, 0, tokens.Count, scopeStack, macroNames, results);
            return;
        }

        switch (tokens[0].Text)
        {
            case "for":
            {
                int inIdx = FindTopLevel(tokens, 1, tokens.Count, "in");
                if (inIdx < 0)
                {
                    WalkExpression(tokens, 1, tokens.Count, scopeStack, macroNames, results);
                    break;
                }

                var loopVars = new List<string>();
                foreach (var (cs, ce) in SplitTopLevelCommas(tokens, 1, inIdx))
                    if (ce > cs && tokens[cs].Kind == TokenKind.Identifier) loopVars.Add(tokens[cs].Text);

                int ifIdx = FindTopLevel(tokens, inIdx + 1, tokens.Count, "if");
                int iterableEnd = ifIdx >= 0 ? ifIdx : tokens.Count;
                WalkExpression(tokens, inIdx + 1, iterableEnd, scopeStack, macroNames, results);

                var newScope = new HashSet<string>(StringComparer.Ordinal);
                foreach (var v in loopVars) newScope.Add(v);
                scopeStack.Push(newScope);

                if (ifIdx >= 0)
                    WalkExpression(tokens, ifIdx + 1, tokens.Count, scopeStack, macroNames, results);
                break;
            }

            case "endfor":
            case "endmacro":
            case "endwith":
                if (scopeStack.Count > 1) scopeStack.Pop();
                break;

            case "set":
            {
                int eq = FindTopLevel(tokens, 1, tokens.Count, "=");
                var names = new List<string>();
                if (eq >= 0)
                {
                    foreach (var (cs, ce) in SplitTopLevelCommas(tokens, 1, eq))
                        if (ce > cs && tokens[cs].Kind == TokenKind.Identifier) names.Add(tokens[cs].Text);
                    WalkExpression(tokens, eq + 1, tokens.Count, scopeStack, macroNames, results);
                }
                else
                {
                    foreach (var (cs, ce) in SplitTopLevelCommas(tokens, 1, tokens.Count))
                        if (ce > cs && tokens[cs].Kind == TokenKind.Identifier) names.Add(tokens[cs].Text);
                }
                foreach (var name in names) scopeStack.Peek().Add(name);
                break;
            }

            case "macro":
            {
                // The macro's own name stays callable for the rest of the template, unlike
                // for/set/with-bound names which are scoped to the block.
                if (tokens.Count > 1 && tokens[1].Kind == TokenKind.Identifier)
                    macroNames.Add(tokens[1].Text);

                int parenOpen = -1;
                for (int i = 2; i < tokens.Count; i++)
                {
                    if (tokens[i].Kind == TokenKind.Punct && tokens[i].Text == "(") { parenOpen = i; break; }
                }
                if (parenOpen < 0)
                {
                    scopeStack.Push(new HashSet<string>(StringComparer.Ordinal));
                    break;
                }

                int depth = 0;
                int parenClose = tokens.Count;
                for (int i = parenOpen; i < tokens.Count; i++)
                {
                    if (tokens[i].Kind == TokenKind.Punct && tokens[i].Text == "(") depth++;
                    else if (tokens[i].Kind == TokenKind.Punct && tokens[i].Text == ")")
                    {
                        depth--;
                        if (depth == 0) { parenClose = i; break; }
                    }
                }

                var newScope = new HashSet<string>(StringComparer.Ordinal);
                foreach (var (cs, ce) in SplitTopLevelCommas(tokens, parenOpen + 1, parenClose))
                {
                    if (ce <= cs) continue;
                    int eq = FindTopLevel(tokens, cs, ce, "=");
                    if (tokens[cs].Kind == TokenKind.Identifier) newScope.Add(tokens[cs].Text);
                    if (eq >= 0)
                        WalkExpression(tokens, eq + 1, ce, scopeStack, macroNames, results);
                }
                scopeStack.Push(newScope);
                break;
            }

            case "with":
            {
                int asIdx = FindTopLevel(tokens, 1, tokens.Count, "as");
                var boundNames = new List<string>();
                if (asIdx >= 0)
                {
                    foreach (var (cs, ce) in SplitTopLevelCommas(tokens, 1, tokens.Count))
                    {
                        int localAs = FindTopLevel(tokens, cs, ce, "as");
                        if (localAs < 0) { WalkExpression(tokens, cs, ce, scopeStack, macroNames, results); continue; }
                        WalkExpression(tokens, cs, localAs, scopeStack, macroNames, results);
                        if (localAs + 1 < ce && tokens[localAs + 1].Kind == TokenKind.Identifier)
                            boundNames.Add(tokens[localAs + 1].Text);
                    }
                }
                else
                {
                    foreach (var (cs, ce) in SplitTopLevelCommas(tokens, 1, tokens.Count))
                    {
                        int eq = FindTopLevel(tokens, cs, ce, "=");
                        if (eq < 0) { WalkExpression(tokens, cs, ce, scopeStack, macroNames, results); continue; }
                        if (tokens[cs].Kind == TokenKind.Identifier) boundNames.Add(tokens[cs].Text);
                        WalkExpression(tokens, eq + 1, ce, scopeStack, macroNames, results);
                    }
                }

                var newScope = new HashSet<string>(StringComparer.Ordinal);
                foreach (var name in boundNames) newScope.Add(name);
                scopeStack.Push(newScope);
                break;
            }

            case "if":
            case "elif":
                WalkExpression(tokens, 1, tokens.Count, scopeStack, macroNames, results);
                break;

            case "else":
            case "endif":
            case "endset":
                break;

            case "block":
                if (tokens.Count > 2) WalkExpression(tokens, 2, tokens.Count, scopeStack, macroNames, results);
                break;

            case "endblock":
            case "raw":
            case "endraw":
                break;

            default:
                // Unrecognized statement keyword (call/filter/endfilter/extends/include/import/
                // namespace/do/continue/break/...) — walk everything after the leading keyword
                // generically in the current scope, binding nothing.
                WalkExpression(tokens, 1, tokens.Count, scopeStack, macroNames, results);
                break;
        }
    }

    private static void WalkExpression(List<Token> tokens, int start, int end, Stack<HashSet<string>> scopeStack, HashSet<string> macroNames, Dictionary<string, List<string>> results)
    {
        for (int i = start; i < end; i++)
        {
            var token = tokens[i];
            if (token.Kind != TokenKind.Identifier) continue;
            var text = token.Text;

            if (i > 0 && tokens[i - 1].Kind == TokenKind.Punct && tokens[i - 1].Text == ".") continue;
            if (i > 0 && tokens[i - 1].Kind == TokenKind.Op && tokens[i - 1].Text == "|") continue;
            if (i > 0 && tokens[i - 1].Text == "is") continue;
            if (i > 1 && tokens[i - 1].Text == "not" && tokens[i - 2].Text == "is") continue;

            if (KeywordOperators.Contains(text)) continue;
            if (Builtins.Contains(text)) continue;
            if (macroNames.Contains(text)) continue;
            if (IsBound(text, scopeStack)) continue;
            if (StandardContextVariables.Contains(text)) continue;

            if (!results.TryGetValue(text, out var list))
            {
                list = new List<string>();
                results[text] = list;
            }
            CollectLiteralValues(tokens, i, list);
        }
    }

    private static bool IsBound(string name, Stack<HashSet<string>> scopeStack)
    {
        foreach (var scope in scopeStack)
            if (scope.Contains(name)) return true;
        return false;
    }

    private static void CollectLiteralValues(List<Token> tokens, int idx, List<string> sink)
    {
        int n = tokens.Count;

        // NAME == "str" / NAME != "str"
        if (idx + 2 < n && tokens[idx + 1].Kind == TokenKind.Op && tokens[idx + 1].Text is "==" or "!="
            && tokens[idx + 2].Kind == TokenKind.String)
            sink.Add(tokens[idx + 2].Text);

        // "str" == NAME / "str" != NAME
        if (idx - 2 >= 0 && tokens[idx - 1].Kind == TokenKind.Op && tokens[idx - 1].Text is "==" or "!="
            && tokens[idx - 2].Kind == TokenKind.String)
            sink.Add(tokens[idx - 2].Text);

        // NAME in [ "a", "b", ... ]
        if (idx + 2 < n && tokens[idx + 1].Kind == TokenKind.Identifier && tokens[idx + 1].Text == "in"
            && tokens[idx + 2].Kind == TokenKind.Punct && tokens[idx + 2].Text == "[")
        {
            int depth = 0;
            for (int j = idx + 2; j < n; j++)
            {
                if (tokens[j].Kind == TokenKind.Punct && tokens[j].Text == "[") depth++;
                else if (tokens[j].Kind == TokenKind.Punct && tokens[j].Text == "]") { depth--; if (depth == 0) break; }
                else if (tokens[j].Kind == TokenKind.String) sink.Add(tokens[j].Text);
            }
        }

        // NAME | default("val")
        if (idx + 3 < n && tokens[idx + 1].Kind == TokenKind.Op && tokens[idx + 1].Text == "|"
            && tokens[idx + 2].Kind == TokenKind.Identifier && tokens[idx + 2].Text == "default"
            && tokens[idx + 3].Kind == TokenKind.Punct && tokens[idx + 3].Text == "(")
        {
            int depth = 0;
            for (int j = idx + 3; j < n; j++)
            {
                if (tokens[j].Kind == TokenKind.Punct && tokens[j].Text == "(") depth++;
                else if (tokens[j].Kind == TokenKind.Punct && tokens[j].Text == ")") { depth--; if (depth == 0) break; }
                else if (tokens[j].Kind == TokenKind.String) sink.Add(tokens[j].Text);
            }
        }
    }

    private static int FindTopLevel(List<Token> tokens, int start, int end, string text)
    {
        int depth = 0;
        for (int i = start; i < end; i++)
        {
            var t = tokens[i];
            if (t.Kind == TokenKind.Punct)
            {
                if (t.Text is "(" or "[" or "{") { depth++; continue; }
                if (t.Text is ")" or "]" or "}") { depth--; continue; }
            }
            if (depth == 0 && t.Text == text) return i;
        }
        return -1;
    }

    private static List<(int Start, int End)> SplitTopLevelCommas(List<Token> tokens, int start, int end)
    {
        var ranges = new List<(int, int)>();
        int depth = 0;
        int chunkStart = start;
        for (int i = start; i < end; i++)
        {
            var t = tokens[i];
            if (t.Kind != TokenKind.Punct) continue;

            if (t.Text is "(" or "[" or "{") { depth++; continue; }
            if (t.Text is ")" or "]" or "}") { depth--; continue; }
            if (t.Text == "," && depth == 0)
            {
                ranges.Add((chunkStart, i));
                chunkStart = i + 1;
            }
        }
        if (chunkStart < end) ranges.Add((chunkStart, end));
        return ranges;
    }

    #endregion
}
