using System.Text.Json;

using Microsoft.Extensions.Logging;

using LR.Core.Data;
using LR.Core.Models;
using LR.Core.Models.OpenAI;
using LR.Core.Models.OpenAI.Responses;

namespace LR.Core.Services;

/// <summary>
/// Reconstructs the full Chat Completions message list for a Responses API turn by walking the
/// `previous_response_id` chain of <see cref="StoredResponse"/> rows (each row stores only its
/// own turn's input/output, not a denormalized transcript) and appending the new turn's input.
/// </summary>
public class ResponseChainBuilder
{
    /// <summary>Safety cap on chain depth — guards against pathological/cyclic chains.</summary>
    private const int MaxChainDepth = 50;

    private static readonly JsonSerializerOptions InputJsonOptions = new() { Converters = { new ResponseInputListConverter() } };

    private readonly LRDbContext _db;
    private readonly ILogger<ResponseChainBuilder> _logger;

    public ResponseChainBuilder(LRDbContext db, ILogger<ResponseChainBuilder> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<List<ChatMessage>> BuildMessagesAsync(
        string? previousResponseId, string? instructions, List<ResponseInputItem> newInput, CancellationToken ct)
    {
        var hops = new List<StoredResponse>();
        var cursor = previousResponseId;
        for (int depth = 0; cursor is not null && depth < MaxChainDepth; depth++)
        {
            var row = await _db.StoredResponses.FindAsync(new object?[] { cursor }, ct);
            if (row is null) break; // dangling/expired/store:false ancestor — stop, don't fail the request
            hops.Add(row);
            if (row.PreviousResponseId == cursor) break; // self-reference guard
            cursor = row.PreviousResponseId;
        }
        if (cursor is not null)
        {
            _logger.LogWarning("previous_response_id chain exceeded {MaxDepth} hops; truncating at the oldest reachable response.", MaxChainDepth);
        }
        hops.Reverse(); // oldest first

        var messages = new List<ChatMessage>();
        if (!string.IsNullOrEmpty(instructions))
            messages.Add(new ChatMessage { Role = "system", Content = ChatMessageContent.FromText(instructions) });

        foreach (var hop in hops)
        {
            messages.AddRange(MapInputItemsToMessages(DeserializeInputItems(hop.OwnInputItemsJson)));
            messages.AddRange(MapOutputItemsToMessages(DeserializeOutputItems(hop.OwnOutputItemsJson)));
        }

        messages.AddRange(MapInputItemsToMessages(newInput));
        return messages;
    }

    public static List<ChatMessage> MapInputItemsToMessages(List<ResponseInputItem> items)
    {
        var messages = new List<ChatMessage>();
        foreach (var item in items)
        {
            switch (item.Kind)
            {
                case ResponseInputItemKind.Message:
                    messages.Add(new ChatMessage
                    {
                        Role = item.Role == "developer" ? "system" : item.Role,
                        Content = MapContent(item.Content)
                    });
                    break;

                case ResponseInputItemKind.FunctionCallOutput:
                    messages.Add(new ChatMessage
                    {
                        Role = "tool",
                        ToolCallId = item.CallId,
                        Content = ChatMessageContent.FromText(item.Output ?? string.Empty)
                    });
                    break;

                case ResponseInputItemKind.FunctionCall:
                    messages.Add(new ChatMessage
                    {
                        Role = "assistant",
                        ToolCalls = new List<ChatToolCall>
                        {
                            new()
                            {
                                Id = item.CallId ?? item.Id ?? string.Empty,
                                Type = "function",
                                Function = new ChatToolCallFunction { Name = item.Name ?? string.Empty, Arguments = item.Arguments ?? string.Empty }
                            }
                        }
                    });
                    break;

                // Reasoning / Unsupported items are not replayable to a Chat Completions backend
                // and are skipped here — Unsupported items in NEW input are rejected up front by
                // the handler before this is ever called on them.
            }
        }
        return messages;
    }

    public static List<ChatMessage> MapOutputItemsToMessages(List<ResponseOutputItem> items)
    {
        // A single assistant turn in Chat Completions carries both its text content and any
        // tool calls together, so fold all output items from one hop into one assistant message.
        string? text = null;
        List<ChatToolCall>? toolCalls = null;

        foreach (var item in items)
        {
            if (item.Type == "message" && item.Content is not null)
            {
                var textParts = item.Content.Where(c => c.Type == "output_text").Select(c => c.Text ?? string.Empty);
                text = (text ?? string.Empty) + string.Concat(textParts);
            }
            else if (item.Type == "function_call")
            {
                toolCalls ??= new List<ChatToolCall>();
                toolCalls.Add(new ChatToolCall
                {
                    Id = item.CallId ?? item.Id,
                    Type = "function",
                    Function = new ChatToolCallFunction { Name = item.Name ?? string.Empty, Arguments = item.Arguments ?? string.Empty }
                });
            }
            // "reasoning" items are intentionally not replayed to the backend.
        }

        if (text is null && toolCalls is null) return new List<ChatMessage>();

        return new List<ChatMessage>
        {
            new()
            {
                Role = "assistant",
                Content = text is not null ? ChatMessageContent.FromText(text) : null,
                ToolCalls = toolCalls
            }
        };
    }

    private static ChatMessageContent? MapContent(ResponseInputContent? content)
    {
        if (content is null) return null;
        if (content.Text is not null) return ChatMessageContent.FromText(content.Text);
        if (content.Parts is { Count: > 0 })
        {
            var parts = content.Parts.Select(p => new ChatMessageContentPart
            {
                Type = p.Type switch
                {
                    "input_image" => "image_url",
                    "input_file" => "input_file",
                    _ => "text" // input_text, output_text, refusal
                },
                Text = p.Type is "input_text" or "output_text" or "refusal" ? p.Text : null,
                ImageUrl = p.Type == "input_image" ? new ChatImageUrlInput { Url = p.ImageUrl ?? string.Empty, Detail = p.Detail } : null,
                InputFile = p.Type == "input_file" ? new ChatInputFile { FileId = p.FileId ?? string.Empty, Filename = p.Filename } : null
            }).ToList();
            return ChatMessageContent.FromParts(parts);
        }
        return null;
    }

    public static string SerializeInputItems(List<ResponseInputItem> items) => JsonSerializer.Serialize(items, InputJsonOptions);

    public static List<ResponseInputItem> DeserializeInputItems(string json) =>
        string.IsNullOrEmpty(json) ? new List<ResponseInputItem>() : JsonSerializer.Deserialize<List<ResponseInputItem>>(json, InputJsonOptions) ?? new List<ResponseInputItem>();

    public static string SerializeOutputItems(List<ResponseOutputItem> items) => JsonSerializer.Serialize(items);

    public static List<ResponseOutputItem> DeserializeOutputItems(string json) =>
        string.IsNullOrEmpty(json) ? new List<ResponseOutputItem>() : JsonSerializer.Deserialize<List<ResponseOutputItem>>(json) ?? new List<ResponseOutputItem>();
}
