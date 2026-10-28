using System.Text.Json;
using System.Text.RegularExpressions;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Logging;
using WoodsyCart.Domain;

namespace WoodsyCart.Infrastructure.Social;

/// <summary>A drafted answer. Complete = every question was answered from real product details (may be sent automatically);
/// Reply null = leave it to a person (complaint, unclear, off-topic).</summary>
public record CommentSuggestion(string? Reply, string Topic, string Source, bool Complete, string? Why = null);

/// <summary>
/// Drafts answers to product questions in comments ("price?", "size?", "colours?", "kitne ka hai") from the product's own
/// details. Keyword rules handle the usual questions in English and common Indian-language words; Claude (when switched on,
/// with an API key from Admin) handles what the rules can't place. Complaints and anything unclear are never answered.
/// </summary>
public partial class CommentAssistant(ILogger<CommentAssistant> logger)
{
    public enum Topic { Price, Size, Colour, Material, Delivery, Payment, Assembly, Warranty, Stock, Order }

    // ---------- Rules ----------

    private static readonly (Topic Topic, Regex Words)[] Rules =
    [
        (Topic.Price, Words(@"price|prices|pp|pls price|rate|rates|cost|costs|amount|how much|howmuch|mrp|kitna|kitne|kitni|kimat|keemat|qeemat|daam|bele|vila|vilai|dhara|dhar|evvalavu|ethra|entha|₹|rs|inr|offer price|discount")),
        (Topic.Size, Words(@"size|sizes|dimension|dimensions|measurement|measurements|length|width|breadth|height|depth|how big|inch|inches|feet|ft|cm|lambai|chaudai")),
        (Topic.Colour, Words(@"colou?rs?|shades?|rang|finish|finishes")),
        (Topic.Material, Words(@"material|wood|wooden|teak|sheesham|rosewood|engineered|plywood|mdf|particle ?board|metal|steel|made of|quality")),
        (Topic.Delivery, Words(@"delivery|deliver|delivered|shipping|ship|pincode|pin code|how many days|when will")),
        (Topic.Payment, Words(@"cod|cash on delivery|emi|no cost emi|payment|pay|upi|card|installments?")),
        (Topic.Assembly, Words(@"assembl\w*|installation|install|fitting|carpenter")),
        (Topic.Warranty, Words(@"warranty|guarantee|guaranty")),
        (Topic.Stock, Words(@"available|availability|in stock|stock|sold out")),
        (Topic.Order, Words(@"how to (buy|order)|order|buy|purchase|link|website|where (can|to)|dm|details|info"))
    ];

    /// <summary>Complaints, refunds and anger go to a person, never to the assistant.</summary>
    private static readonly Regex Unhappy = Words(@"worst|bad|fraud|scam|cheat\w*|fake|complain\w*|refund|return|broken|damaged?|not received|never received|poor|waste|angry|disappointed|useless|issue|problem|late|delay\w*|rude|pathetic|bekar|bakwas|ghatiya");

    private static Regex Words(string alternatives) => new($@"(?<![\p{{L}}\p{{N}}])({alternatives})(?![\p{{L}}\p{{N}}])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>What the comment asks about (empty = the rules can't tell), and whether it sounds unhappy.</summary>
    public static (List<Topic> Topics, bool Unhappy) Read(string text)
    {
        var t = text.ToLowerInvariant();
        var topics = Rules.Where(r => r.Words.IsMatch(t)).Select(r => r.Topic).ToList();
        // "Order" alone ("dm", "details") is a buying signal: answer it with the price and how to order
        if (topics is [Topic.Order]) topics.Insert(0, Topic.Price);
        return (topics, Unhappy.IsMatch(t));
    }

    /// <summary>The answer from the product's details. Instagram replies mention the person; Instagram doesn't make links in
    /// comments clickable, so it says the product code to search instead.</summary>
    public static CommentSuggestion FromRules(List<Topic> topics, ProductFacts f, string author, SocialPlatform platform, int seed)
    {
        var lines = new List<string>();
        var complete = true;
        foreach (var t in topics.Distinct())
        {
            switch (t)
            {
                case Topic.Price:
                    lines.Add(f.Mrp is null ? $"Price: {f.Price}" : $"Price: {f.Price} (MRP {f.Mrp}, you save {f.Saving})"
                        + (f.Offer is null ? "" : $" — {f.Offer} offer"));
                    break;
                case Topic.Size:
                    if (f.Size is { } s) lines.Add($"Size: {s}");
                    else { lines.Add("Size: all measurements are on the product page."); complete = false; }
                    foreach (var o in f.Options.Where(o => o.StartsWith("Size", StringComparison.OrdinalIgnoreCase))) lines.Add(o);
                    break;
                case Topic.Colour:
                    lines.Add(f.Colours.Count switch
                    {
                        0 => "Colour: as shown in the photos.",
                        1 => $"Colour: {f.Colours[0]}",
                        _ => $"Colours: {string.Join(", ", f.Colours)}"
                    });
                    break;
                case Topic.Material:
                    if (f.Material is { } m) lines.Add($"Material: {m}");
                    else { lines.Add("Material: the details are on the product page."); complete = false; }
                    break;
                case Topic.Delivery:
                    lines.Add(f.Assembly ? $"Delivered and assembled at your home in {f.City} in about {f.DeliveryDays} days"
                        : $"Delivered in {f.City} in about {f.DeliveryDays} days");
                    break;
                case Topic.Payment:
                    // Payment terms change; never promised automatically
                    lines.Add("Payment options are shown at checkout on our website.");
                    complete = false;
                    break;
                case Topic.Assembly:
                    lines.Add(f.Assembly ? "Assembly at your home is included." : "This one doesn't need assembly by us.");
                    break;
                case Topic.Warranty:
                    if (f.Warranty is { } w) lines.Add($"Warranty: {w}");
                    else { lines.Add("Warranty: the details are on the product page."); complete = false; }
                    break;
                case Topic.Stock:
                    lines.Add(f.InStock ? "Yes, it's available ✅" : "It's out of stock right now — message us and we'll tell you when it's back.");
                    break;
                case Topic.Order:
                    break;   // the closing line says how to order
            }
        }
        var insta = platform is SocialPlatform.Instagram or SocialPlatform.InstagramReel;
        string[] openers = insta ? [$"Hi @{author}!", $"Hello @{author} 😊", $"Thanks for asking, @{author}!"] : ["Hi!", "Hello 😊", "Thanks for asking!"];
        var where = insta ? $"Search {f.Code} on {f.Website}" : $"Order here: {f.Link}";
        var close = f.WhatsApp is { } wa ? $"{where} or WhatsApp {wa} 😊" : $"{where} 😊";
        var reply = openers[seed % openers.Length] + "\n" + string.Join("\n", lines) + "\n" + close;
        return new CommentSuggestion(reply, string.Join(", ", topics.Distinct()), "Rules", complete);
    }

    // ---------- Claude ----------

    private const string System =
        """
        You answer comments on WoodsyCart's Instagram, Facebook and Threads posts. WoodsyCart sells furniture online in India
        (woodsycart.com); each post shows one product, whose details you are given.

        Decide whether the comment is a question about this product that the given details answer. Answer only from the
        details: never invent sizes, colours, materials, prices, discounts, delivery dates or promises. If the comment is a
        complaint, unhappy, abusive, spam, about an order or a refund, about another product, or a question the details don't
        answer, set answerable to false and leave reply empty: a person will handle it.

        When answerable, write the reply as WoodsyCart: short (2-5 lines), friendly, at most one emoji, in the commenter's
        language (English if unsure; prices always in ₹ digits). Start with the given greeting. End with the given closing line,
        exactly as given. No hashtags. Plain text only.
        """;

    /// <summary>Asks Claude to sort and answer a comment the rules couldn't place. Null when Claude declines or fails.</summary>
    public async Task<CommentSuggestion?> FromAiAsync(string apiKey, string model, ProductFacts f, string author, string text,
        IReadOnlyList<string> earlier, SocialPlatform platform, CancellationToken ct)
    {
        var insta = platform is SocialPlatform.Instagram or SocialPlatform.InstagramReel;
        var details = JsonSerializer.Serialize(new
        {
            name = f.Name, code = f.Code, price = f.Price, mrp = f.Mrp, saving = f.Saving, offer = f.Offer, size = f.Size,
            colours = f.Colours, options = f.Options, material = f.Material, warranty = f.Warranty,
            delivery = f.Assembly ? $"delivered and assembled at home in {f.City} in about {f.DeliveryDays} days" : $"delivered in {f.City} in about {f.DeliveryDays} days",
            in_stock = f.InStock
        });
        var greeting = insta ? $"Hi @{author}!" : "Hi!";
        var closing = insta ? $"Search {f.Code} on {f.Website}" + (f.WhatsApp is null ? "" : $" or WhatsApp {f.WhatsApp}")
            : $"Order here: {f.Link}" + (f.WhatsApp is null ? "" : $" or WhatsApp {f.WhatsApp}");
        var prompt = $"""
            <product>{details}</product>
            <platform>{platform}</platform>
            <greeting>{greeting}</greeting>
            <closing>{closing}</closing>
            <earlier_in_conversation>{string.Join("\n", earlier.TakeLast(5))}</earlier_in_conversation>
            <comment author="{author}">{text}</comment>
            """;
        try
        {
            var client = new AnthropicClient { ApiKey = apiKey };
            var response = await client.Messages.Create(new MessageCreateParams
            {
                Model = model,
                MaxTokens = 4000,
                System = System,
                OutputConfig = new OutputConfig
                {
                    Effort = Effort.Low,
                    Format = new JsonOutputFormat
                    {
                        Schema = new Dictionary<string, JsonElement>
                        {
                            ["type"] = JsonSerializer.SerializeToElement("object"),
                            ["properties"] = JsonSerializer.SerializeToElement(new
                            {
                                answerable = new { type = "boolean" },
                                topic = new { type = "string", description = "What was asked, e.g. Price, Size, Colour" },
                                reply = new { type = "string" }
                            }),
                            ["required"] = JsonSerializer.SerializeToElement(new[] { "answerable", "topic", "reply" }),
                            ["additionalProperties"] = JsonSerializer.SerializeToElement(false)
                        }
                    }
                },
                Messages = [new() { Role = Role.User, Content = prompt }]
            }, cancellationToken: ct);
            if (response.StopReason == "refusal") return null;
            var json = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(b => b.Text));
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var answerable = r.GetProperty("answerable").GetBoolean();
            var reply = r.GetProperty("reply").GetString()?.Trim();
            var topic = r.GetProperty("topic").GetString() ?? "";
            logger.LogInformation("Claude read a comment ({Topic}, answerable {Answerable}); tokens in {In}, out {Out}",
                topic, answerable, response.Usage.InputTokens, response.Usage.OutputTokens);
            return answerable && !string.IsNullOrWhiteSpace(reply)
                ? new CommentSuggestion(reply.Length > 2000 ? reply[..2000] : reply, topic, "AI", true)
                : new CommentSuggestion(null, topic, "AI", false, "Claude: needs a person");
        }
        catch (Exception ex) when (ex is AnthropicApiException or HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            logger.LogWarning("Claude could not read the comment: {Message}", ex.Message);
            return null;
        }
    }
}
