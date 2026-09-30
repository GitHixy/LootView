#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Dalamud.Game;
using Dalamud.Game.Chat;
using LootView.Models;
using Lumina.Excel.Sheets;

namespace LootView.Services;

/// <summary>
/// How LootView reads loot, in every client language. Instead of reading English chat text,
/// it listens to the game's log messages, which arrive as a LogMessage row id plus typed
/// parameters (item ids, quantities, roll values, the player they're about) that are the same
/// in every language.
///
/// Which rows matter, and which parameter holds what, is worked out once from the English text of
/// the LogMessage sheet: "&lt;ennoun(Item,1,lnum2,...)&gt;" means parameter 2 is the item, whatever
/// language the message is later shown in.
/// </summary>
public partial class LootTrackingService
{
    private enum LogEvent
    {
        LootAdded,
        CastLot,
        Roll,
        Obtain,
    }

    /// <summary>
    /// What a LogMessage row means, and where its values are. Parameter numbers are 1-based, as in
    /// the sheet. Currencies the text names outright (gil, MGP, Grand Company seals) have no item
    /// parameter: their item is <see cref="FixedItemId"/>, or <see cref="ItemOffset"/> plus the parameter.
    /// </summary>
    private sealed record LogTemplate(
        LogEvent Event,
        int ItemParam,
        int QuantityParam,
        bool ActorIsSource,
        LootSource Source,
        uint FixedItemId = 0,
        uint ItemOffset = 0,
        int RollTypeParam = 0,
        int RollValueParam = 0,
        int SizeParam = 0,
        int SizeDecimalParam = 0);

    private const uint GilItemId = 1;
    private const uint MgpItemId = 29;

    /// <summary>Storm, Serpent and Flame Seals are items 20-22, in Grand Company order (1-3).</summary>
    private const uint SealItemOffset = 19;

    private Dictionary<uint, LogTemplate>? logTemplates;
    private HashSet<string> needWords = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> greedWords = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether loot is read from log messages. The old English chat reader only takes over on an
    /// English client that asked for it in Diagnostics, or if the log messages couldn't be mapped.
    /// </summary>
    public bool UsesLogMessages =>
        logTemplates != null &&
        !(Plugin.ClientState.ClientLanguage == ClientLanguage.English && configService.Configuration.UseLegacyChatReader);

    private void InitializeLogMessages()
    {
        try
        {
            logTemplates = BuildLogTemplates();
            (needWords, greedWords) = BuildRollWords();
            Plugin.ChatGui.LogMessage += OnLogMessage;

            Plugin.Log.Info($"Log message loot detection ready: {logTemplates.Count} log messages mapped, " +
                            $"client language {Plugin.ClientState.ClientLanguage}, active: {UsesLogMessages}");
        }
        catch (Exception ex)
        {
            logTemplates = null;
            Plugin.Log.Error(ex, "Failed to set up log message loot detection; falling back to the English chat reader");
        }
    }

    private void DisposeLogMessages()
    {
        Plugin.ChatGui.LogMessage -= OnLogMessage;
    }

    // ------------------------------------------------------------------
    // Reading messages
    // ------------------------------------------------------------------

    private void OnLogMessage(ILogMessage message)
    {
        try
        {
            if (!UsesLogMessages || logTemplates == null || !logTemplates.TryGetValue(message.LogMessageId, out var template))
                return;

            if (configService.Configuration.EnableDebugLogging)
                Plugin.Log.Debug($"Log message {message.LogMessageId} ({template.Event}): {DescribeParameters(message)}");

            if (template.Event is LogEvent.LootAdded or LogEvent.CastLot or LogEvent.Roll &&
                !configService.Configuration.EnableRollTracking)
                return;

            var localName = Plugin.ObjectTable.LocalPlayer?.Name.TextValue ?? "You";
            var actorName = template.ActorIsSource ? message.SourceEntity?.Name.ExtractText() : null;
            var isSelf = string.IsNullOrEmpty(actorName) || SameName(actorName, localName);
            var playerName = isSelf ? localName : actorName!;

            var rawItemId = template.FixedItemId > 0
                ? (int)template.FixedItemId
                : GetInt(message, template.ItemParam) + (int)template.ItemOffset;
            if (rawItemId is not > 0)
            {
                Plugin.Log.Warning($"Log message {message.LogMessageId} has no item in parameter {template.ItemParam}: {DescribeParameters(message)}");
                return;
            }

            var isHq = rawItemId.Value > 1_000_000;
            var itemData = GetItemDataById(NormalizeItemId((uint)rawItemId.Value));
            if (itemData == null)
                return;

            switch (template.Event)
            {
                case LogEvent.LootAdded:
                    StartRollSession(itemData.Value);
                    break;

                case LogEvent.CastLot:
                    // Your own choice is read from the loot list instead.
                    if (!isSelf)
                        RecordCastLot(playerName, itemData.Value);
                    break;

                case LogEvent.Roll:
                    RecordLoggedRoll(message, template, playerName, itemData.Value);
                    break;

                case LogEvent.Obtain:
                    var quantity = GetInt(message, template.QuantityParam) is > 0 and var q ? (uint)q : 1u;
                    var name = itemData.Value.Name;
                    if (template.SizeParam > 0 && GetInt(message, template.SizeParam) is { } size)
                        name = $"{name} [{size}.{GetInt(message, template.SizeDecimalParam) ?? 0} ilms]";

                    RecordObtain(playerName, isSelf, itemData.Value with { Name = name }, name, quantity, isHq,
                        template.Source, linkRolls: template.Source == LootSource.Unknown);
                    break;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, $"Error processing log message {message.LogMessageId}");
        }
    }

    private void RecordLoggedRoll(ILogMessage message, LogTemplate template, string playerName,
        (uint ItemId, uint IconId, uint Rarity, string Name) itemData)
    {
        var kindText = GetString(message, template.RollTypeParam)?.Trim() ?? string.Empty;
        var value = GetInt(message, template.RollValueParam) ?? 0;

        string kind;
        if (needWords.Contains(kindText))
            kind = RollKind.Need;
        else if (greedWords.Contains(kindText))
            kind = RollKind.Greed;
        else
        {
            Plugin.Log.Warning($"Unrecognised roll type '{kindText}' in log message {message.LogMessageId}; recording it as Greed");
            kind = RollKind.Greed;
        }

        RecordRoll(playerName, itemData, kind, value);
    }

    /// <summary>Log message parameters are numbered from 1 in the sheet and from 0 in Dalamud.</summary>
    private static int? GetInt(ILogMessage message, int param)
        => param > 0 && message.TryGetIntParameter(param - 1, out var value) ? value : null;

    private static string? GetString(ILogMessage message, int param)
        => param > 0 && message.TryGetStringParameter(param - 1, out var value) ? value.ExtractText() : null;

    private static string DescribeParameters(ILogMessage message)
    {
        var sb = new StringBuilder();
        sb.Append($"source={message.SourceEntity?.Name.ExtractText() ?? "-"}");
        for (var i = 0; i < message.ParameterCount; i++)
        {
            if (message.TryGetIntParameter(i, out var n))
                sb.Append($", p{i + 1}={n}");
            else if (message.TryGetStringParameter(i, out var s))
                sb.Append($", p{i + 1}=\"{s.ExtractText()}\"");
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------
    // Mapping the LogMessage sheet
    // ------------------------------------------------------------------

    private static readonly Regex ItemRef = new(@"(?:ennoun\(Item,[^,]+,|sheet\(Item,)lnum(\d+)(?:,([^,)]+))?", RegexOptions.Compiled);
    private static readonly Regex NumberRef = new(@"(?:num|kilo)\(lnum(\d+)[,)]", RegexOptions.Compiled);
    private static readonly Regex OneCheck = new(@"\[lnum(\d+)(?:==|<=)1\]", RegexOptions.Compiled);
    private static readonly Regex StringRef = new(@"string\(lstr(\d+)\)", RegexOptions.Compiled);
    private static readonly Regex RollValue = new(@"<num\(lnum(\d+)\)>!", RegexOptions.Compiled);
    private static readonly Regex GrandCompanyRef = new(@"\[lnum(\d+)==1\],Storm", RegexOptions.Compiled);
    private static readonly Regex FishSize = new(@"measuring <num\(lnum(\d+)\)>\.<num\(lnum(\d+)\)>", RegexOptions.Compiled);

    // Messages about loot you did *not* get, or about something else entirely.
    private static readonly string[] Excluded =
    [
        "Unable", "unable", "cannot", "can't", "First obtain", "Obtain presents", "overwritten", "to gather this item",
        "desynthesiz", "party obtains", "ifself(", "pcname(", "may now obtain", "expending", "EventItem",
    ];

    /// <summary>Classifies every LogMessage row from its English text.</summary>
    private static Dictionary<uint, LogTemplate> BuildLogTemplates()
    {
        var templates = new Dictionary<uint, LogTemplate>();
        var sheet = Plugin.DataManager.GetExcelSheet<LogMessage>(ClientLanguage.English);
        var clientSheet = Plugin.DataManager.GetExcelSheet<LogMessage>();

        foreach (var row in sheet)
        {
            var text = row.Text.ToMacroString();
            if (text.Length == 0 || Excluded.Any(text.Contains))
                continue;

            if (Classify(text) is not { } template)
                continue;

            // A few rows name a key item in some languages and a regular item in others; the id
            // would then point into the wrong sheet, so leave those out.
            if (clientSheet.TryGetRow(row.RowId, out var local) && local.Text.ToMacroString().Contains("EventItem"))
                continue;

            templates[row.RowId] = template;
        }

        return templates;
    }

    private static LogTemplate? Classify(string text)
    {
        var actorIsSource = text.Contains("[gstr1==gstr2]");
        var items = ItemRef.Matches(text);
        var lastItem = items.Count > 0 ? items[^1] : null;
        var itemParam = lastItem != null ? int.Parse(lastItem.Groups[1].Value) : 0;

        if (text.Contains("been added to the loot list"))
            return itemParam > 0 ? new LogTemplate(LogEvent.LootAdded, itemParam, QuantityParam(text, lastItem), false, LootSource.Chest) : null;

        if (text.Contains(" lot for ") && actorIsSource)
            return itemParam > 0 ? new LogTemplate(LogEvent.CastLot, itemParam, 0, true, LootSource.Chest) : null;

        if (text.Contains("roll,rolls)>") && StringRef.Match(text) is { Success: true } kind && RollValue.Match(text) is { Success: true } value)
        {
            return itemParam > 0
                ? new LogTemplate(LogEvent.Roll, itemParam, 0, actorIsSource, LootSource.Chest,
                    RollTypeParam: int.Parse(kind.Groups[1].Value), RollValueParam: int.Parse(value.Groups[1].Value))
                : null;
        }

        if (text.Contains("gil has been awarded for using the duty roulette"))
        {
            // "A bonus of [experience and] <kilo(lnum2)> gil..." - the gil is the last number.
            var numbers = NumberRef.Matches(text);
            return numbers.Count > 0
                ? new LogTemplate(LogEvent.Obtain, 0, int.Parse(numbers[^1].Groups[1].Value), false, LootSource.DutyRoulette, FixedItemId: GilItemId)
                : null;
        }

        LootSource? source =
            text.Contains(" measuring ") && (text.Contains("land") || text.Contains("spear")) ? LootSource.Gathering :
            text.Contains("synthesize") ? LootSource.Crafting :
            text.Contains("successfully extracted") || text.Contains("restore the aether") ? LootSource.Extraction :
            text.StartsWith("You exchange ", StringComparison.Ordinal) ? LootSource.Exchange :
            text.Contains("added to your inventory") ? LootSource.Other :
            text.Contains("obtain") ? LootSource.Unknown :
            null;

        if (source == null)
            return null;

        if (itemParam == 0)
            return source == LootSource.Unknown ? ClassifyCurrency(text, actorIsSource) : null;

        if (source == LootSource.Gathering && FishSize.Match(text) is { Success: true } fish)
        {
            // Fish sizes are numbers too, so only an explicit count is a quantity here.
            var count = CountArgument(lastItem);
            return new LogTemplate(LogEvent.Obtain, itemParam, count, actorIsSource, LootSource.Gathering,
                SizeParam: int.Parse(fish.Groups[1].Value), SizeDecimalParam: int.Parse(fish.Groups[2].Value));
        }

        return new LogTemplate(LogEvent.Obtain, itemParam, QuantityParam(text, lastItem), actorIsSource, source.Value);
    }

    /// <summary>"You obtain 1,000 gil.", "... 500 MGP.", "... 800 Serpent Seals." - currencies named in the text.</summary>
    private static LogTemplate? ClassifyCurrency(string text, bool actorIsSource)
    {
        var quantity = QuantityParam(text, null);
        if (quantity == 0)
            return null;

        if (text.Contains(" gil"))
            return new LogTemplate(LogEvent.Obtain, 0, quantity, actorIsSource, LootSource.Unknown, FixedItemId: GilItemId);

        if (text.Contains(" MGP"))
            return new LogTemplate(LogEvent.Obtain, 0, quantity, actorIsSource, LootSource.Unknown, FixedItemId: MgpItemId);

        // "<if([lnum1==1],Storm,<if([lnum1==2],Serpent,Flame)>)> Seals" - the parameter is the Grand Company.
        if (text.Contains("Serpent") && GrandCompanyRef.Match(text) is { Success: true } company)
            return new LogTemplate(LogEvent.Obtain, int.Parse(company.Groups[1].Value), quantity, actorIsSource, LootSource.Unknown, ItemOffset: SealItemOffset);

        return null;
    }

    /// <summary>
    /// Which parameter carries the quantity: the count given to the item's noun, else the first
    /// number printed (skipping "+N%" bonuses), else the one checked against 1, else none.
    /// </summary>
    private static int QuantityParam(string text, Match? lastItem)
    {
        var count = CountArgument(lastItem);
        if (count > 0)
            return count;

        foreach (Match number in NumberRef.Matches(text))
        {
            var close = text.IndexOf(")>", number.Index, StringComparison.Ordinal);
            if (close >= 0 && close + 2 < text.Length && text[close + 2] == '%')
                continue;

            return int.Parse(number.Groups[1].Value);
        }

        return OneCheck.Match(text) is { Success: true } one ? int.Parse(one.Groups[1].Value) : 0;
    }

    private static int CountArgument(Match? item)
    {
        var arg = item?.Groups[2].Value;
        return arg != null && arg.StartsWith("lnum", StringComparison.Ordinal) && int.TryParse(arg[4..], out var n) ? n : 0;
    }

    /// <summary>The words the client uses for Need and Greed, as they appear in roll messages.</summary>
    private static (HashSet<string> Need, HashSet<string> Greed) BuildRollWords()
    {
        var need = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { RollKind.Need };
        var greed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { RollKind.Greed };

        var english = Plugin.DataManager.GetExcelSheet<Addon>(ClientLanguage.English);
        var client = Plugin.DataManager.GetExcelSheet<Addon>();

        foreach (var row in english)
        {
            var word = row.Text.ExtractText();
            var set = word == RollKind.Need ? need : word == RollKind.Greed ? greed : null;
            if (set != null && client.TryGetRow(row.RowId, out var local))
                set.Add(local.Text.ExtractText().Trim());
        }

        return (need, greed);
    }
}
