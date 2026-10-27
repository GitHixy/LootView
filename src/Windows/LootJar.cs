using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility.Raii;
using LootView.Models;
using LootView.UI;
using Lumina.Excel.Sheets;

namespace LootView.Windows;

/// <summary>
/// The loot jar: the loot window's second view, where everything you obtain falls into a glass
/// jar. Each distinct item is one token with its icon, and a repeat drop falls onto the token
/// already there and merges into it, so the jar stays readable however long the session runs.
/// Currencies and crystals are not tokens but sand, settling in layers at the bottom in the
/// order they were earned, each labelled with its name. Moving the window sloshes the contents,
/// and a token can be picked up and thrown.
///
/// The simulation runs in jar units: the body is 1 wide and <see cref="BodyH"/> tall, with y
/// growing downwards from the shoulders, so resizing the window rescales the scene rather than
/// disturbing it.
/// </summary>
public sealed class LootJar
{
    // --- Jar shape, in jar units --------------------------------------------------------------
    private const float BodyH = 1.2f;
    private const float NeckL = 0.27f;
    private const float NeckR = 0.73f;
    private const float NeckTop = -0.15f;
    private const float ShoulderR = 0.2f;
    private const float BottomR = 0.16f;

    // --- Simulation ---------------------------------------------------------------------------
    private const float Step = 1f / 120f;
    private const int MaxSteps = 8;
    private const float Gravity = 3.4f;
    private const float Restitution = 0.22f;
    private const int SolverPasses = 3;

    /// <summary>The tallest the sand may grow, and the combined weight at which it gets there.</summary>
    private const float SandMax = 0.42f;
    private const float SandFullWeight = 7f;
    private const float MinLayer = 0.018f;

    /// <summary>Token size at its largest, and how much of the free space the tokens may fill.</summary>
    private const float MaxTokenRadius = 0.075f;
    private const float FillFraction = 0.5f;

    private const float EmptySeconds = 1.1f;

    private readonly Plugin plugin;
    private readonly Random random = new();

    private readonly List<Token> tokens = new();
    private readonly Dictionary<(uint ItemId, bool IsHq), Token> tokenByKey = new();
    private readonly List<SandLayer> layers = new();
    private readonly Dictionary<string, SandLayer> layerByKey = new();
    private readonly List<Grain> grains = new();
    private readonly List<Drop> drops = new();
    private readonly List<Spark> sparks = new();

    /// <summary>Fixed points the sand's speckle is drawn at, so the grain doesn't shimmer.</summary>
    private readonly Vector2[] speckle;

    private float accumulator;
    private float sandHeight;
    private bool snapSand;

    // Sloshing: the sand surface tilts against sideways acceleration and springs back.
    private float tilt, tiltVelocity;

    // Where the jar was on screen, in jar units, to feel the window being moved.
    private Vector2? lastOrigin;
    private Vector2 originVelocity;
    private int lastFrame;

    /// <summary>Layer names are drawn this much smaller than the window's text.</summary>
    private const float LabelScale = 0.78f;

    private Token? grabbed;
    private Vector2 grabTarget;
    private Token? menuToken;

    private float flash;
    private float emptying = -1f;

    private int dropCount;

    public LootJar(Plugin plugin)
    {
        this.plugin = plugin;

        var seeded = new Random(7);
        speckle = new Vector2[320];
        for (var i = 0; i < speckle.Length; i++)
            speckle[i] = new Vector2(0.03f + (float)seeded.NextDouble() * 0.94f, BodyH - 0.01f - (float)seeded.NextDouble() * (SandMax + 0.05f));
    }

    private sealed class Token
    {
        public uint ItemId;
        public bool IsHq;
        public uint IconId;
        public uint Rarity;
        public string Name = string.Empty;
        public long Quantity;
        public int Drops;
        public DateTime LastAt;
        public Vector2 Pos, Vel;
        public float Radius, Spin;
        public float Pulse;
    }

    private sealed class SandLayer
    {
        public string Key = string.Empty;
        public string Name = string.Empty;
        public uint IconId;
        public Vector4 Color;
        public long Amount;
        public double Landed;
        public float Thickness;
        public DateTime LastAt;
        public readonly Dictionary<uint, long> Parts = new();
    }

    /// <summary>A falling grain of sand, carrying its share of the gain it belongs to.</summary>
    private struct Grain
    {
        public SandLayer Layer;
        public Vector2 Pos, Vel;
        public double Share;
        public float Shade;
    }

    /// <summary>A repeat drop on its way to the token it merges into.</summary>
    private sealed class Drop
    {
        public Token Target = null!;
        public long Quantity;
        public Vector2 Pos, Vel;
        public float Age;
    }

    private struct Spark
    {
        public Vector2 Pos, Vel;
        public float Life, MaxLife;
        public Vector4 Color;
    }

    public bool IsEmpty => tokens.Count == 0 && layers.Count == 0;

    /// <summary>Whether the jar was drawn on the last frame.</summary>
    private bool IsVisible => ImGui.GetFrameCount() - lastFrame <= 1;

    // ============================================================================
    // FILLING
    // ============================================================================

    /// <summary>Fills the jar with what the tracker already holds, settled rather than falling in.</summary>
    public void Seed(IEnumerable<LootItem> items)
    {
        foreach (var item in items.OrderBy(i => i.Timestamp))
            Add(item, false);
        snapSand = true;
    }

    /// <summary>Drops one of your items into the jar. Party members' loot stays in the list.</summary>
    public void Add(LootItem item, bool animate = true)
    {
        if (item.ItemId == 0 || item.Quantity == 0 || !item.IsOwnLoot) return;
        if (plugin.ConfigService.Configuration.BlacklistedItemIds?.Contains(item.ItemId) ?? false) return;

        // Anything arriving while the jar pours out lands in the next one.
        if (emptying >= 0) FinishEmptying();

        // Nobody is watching the jar: drop it straight into place rather than queueing a fall
        // that would all play at once when the view comes back.
        animate &= IsVisible;

        dropCount++;

        if (Sand(item.ItemId) is { } sand)
        {
            AddSand(sand, item, animate);
            return;
        }

        var key = (item.ItemId, item.IsHQ);
        if (tokenByKey.TryGetValue(key, out var token))
        {
            token.Drops++;
            token.LastAt = item.Timestamp;
            if (animate)
            {
                drops.Add(new Drop
                {
                    Target = token,
                    Quantity = item.Quantity,
                    Pos = Mouth(),
                    Vel = new Vector2(Jitter(0.15f), 0.4f),
                });
            }
            else
            {
                token.Quantity += item.Quantity;
            }
            return;
        }

        token = new Token
        {
            ItemId = item.ItemId,
            IsHq = item.IsHQ,
            IconId = item.IconId,
            Rarity = Math.Clamp(item.Rarity, 1u, 7u),
            Name = item.ItemName,
            Quantity = item.Quantity,
            Drops = 1,
            LastAt = item.Timestamp,
            Spin = Jitter(1f),
        };

        if (animate)
        {
            token.Pos = Mouth();
            token.Vel = new Vector2(Jitter(0.3f), 0.5f);
            token.Radius = MaxTokenRadius * 0.4f;
        }
        else
        {
            token.Pos = new Vector2(0.15f + (float)random.NextDouble() * 0.7f, 0.25f + (float)random.NextDouble() * 0.6f);
            token.Radius = MaxTokenRadius * 0.6f;
        }

        tokens.Add(token);
        tokenByKey[key] = token;

        // Something rare, or a collectible you don't have yet, jolts the jar.
        if (animate && (token.Rarity >= 4 || ItemUnlocks.Get(item.ItemId) == UnlockStatus.Locked))
            Bump(1f);
    }

    private void AddSand((string Key, uint BaseItemId) sand, LootItem item, bool animate)
    {
        if (!layerByKey.TryGetValue(sand.Key, out var layer))
        {
            var info = ItemInfo(sand.BaseItemId);
            layer = new SandLayer
            {
                Key = sand.Key,
                Name = info.Name,
                IconId = info.IconId,
                Color = IconColors.Dominant(info.IconId, Theme.Gold),
            };
            layers.Add(layer);
            layerByKey[sand.Key] = layer;
        }

        layer.Amount += item.Quantity;
        layer.LastAt = item.Timestamp;
        layer.Parts.TryGetValue(item.ItemId, out var part);
        layer.Parts[item.ItemId] = part + item.Quantity;

        if (!animate)
        {
            layer.Landed = layer.Amount;
            return;
        }

        // More grains for bigger gains, but never so many that a gil reward becomes a blizzard.
        var count = (int)Math.Clamp(6 + 6 * Math.Log10(1 + item.Quantity), 6, 48);
        for (var i = 0; i < count; i++)
        {
            grains.Add(new Grain
            {
                Layer = layer,
                Pos = Mouth() + new Vector2(Jitter(0.07f), -(float)random.NextDouble() * 0.35f),
                Vel = new Vector2(Jitter(0.12f), 0.2f + (float)random.NextDouble() * 0.4f),
                Share = (double)item.Quantity / count,
                Shade = Jitter(0.12f),
            });
        }
    }

    /// <summary>
    /// What kind of sand an item settles as, if any. Currencies get a layer each; the 18 crystals
    /// share one layer per element, since shards, crystals and clusters of fire are all just fire.
    /// </summary>
    private static (string Key, uint BaseItemId)? Sand(uint itemId)
    {
        if (itemId is >= 2 and <= 19)
        {
            var element = (itemId - 2) % 6;
            return ($"crystal:{element}", 8 + element);
        }

        if (itemId == 1 || ItemInfo(itemId).IsCurrency)
            return ($"item:{itemId}", itemId);

        return null;
    }

    private static readonly Dictionary<uint, (string Name, uint IconId, bool IsCurrency)> Items = new();

    private static (string Name, uint IconId, bool IsCurrency) ItemInfo(uint itemId)
    {
        if (Items.TryGetValue(itemId, out var info)) return info;

        info = (string.Empty, 0, false);
        if (Plugin.DataManager.GetExcelSheet<Item>().TryGetRow(itemId, out var row))
            info = (row.Name.ExtractText(), row.Icon, row.ItemUICategory.RowId == 100);

        Items[itemId] = info;
        return info;
    }

    // ============================================================================
    // PREVIEW
    // ============================================================================

    private readonly Queue<(float At, LootItem Item)> preview = new();
    private float previewClock;

    /// <summary>
    /// Pours a few seconds of sample drops into the jar: gear of every rarity, a minion, repeat
    /// drops that merge, crystals and currencies. They only exist in the jar.
    /// </summary>
    public void Preview()
    {
        preview.Clear();
        previewClock = 0;

        var sheet = Plugin.DataManager.GetExcelSheet<Item>();
        var picks = new List<Item>();
        void Pick(Func<Item, bool> where, int count)
            => picks.AddRange(sheet.Where(i => i.Icon > 0 && !i.Name.IsEmpty && where(i)).Skip(random.Next(40)).Take(count));

        Pick(i => i.Rarity == 1 && i.ItemUICategory.RowId is 47 or 48 or 49, 2); // ore, stone, cloth
        Pick(i => i.Rarity == 2 && i.EquipSlotCategory.RowId != 0 && i.LevelItem.RowId > 500, 2);
        Pick(i => i.Rarity == 3 && i.EquipSlotCategory.RowId != 0 && i.LevelItem.RowId > 500, 2);
        Pick(i => i.Rarity == 4 && i.EquipSlotCategory.RowId != 0, 1);
        Pick(i => i.ItemUICategory.RowId == 81, 1); // a minion

        var at = 0f;
        void Queue(uint itemId, uint quantity, float gap, bool hq = false)
        {
            if (!sheet.TryGetRow(itemId, out var row)) return;
            at += gap;
            preview.Enqueue((at, new LootItem
            {
                ItemId = itemId,
                ItemName = row.Name.ExtractText(),
                IconId = row.Icon,
                Rarity = row.Rarity,
                Quantity = quantity,
                IsHQ = hq,
                IsOwnLoot = true,
                Timestamp = DateTime.Now,
            }));
        }

        Queue(1, 12_400, 0.1f);
        foreach (var item in picks)
            Queue(item.RowId, item.Rarity == 1 ? (uint)random.Next(3, 12) : 1, 0.45f);
        Queue(2, 120, 0.4f);
        Queue(14, 6, 0.3f);
        if (picks.Count > 0) Queue(picks[0].RowId, 5, 0.6f);
        Queue(20, 1_850, 0.5f);
        Queue(28, 450, 0.5f);
        if (picks.Count > 1) Queue(picks[1].RowId, 3, 0.4f, true);
        Queue(8, 40, 0.5f);
        Queue(1, 48_000, 0.7f);
        if (picks.Count > 0) Queue(picks[0].RowId, 9, 0.5f);
    }

    private void UpdatePreview(float dt)
    {
        if (preview.Count == 0) return;

        previewClock += dt;
        while (preview.Count > 0 && preview.Peek().At <= previewClock)
        {
            var item = preview.Dequeue().Item;
            item.Timestamp = DateTime.Now;
            Add(item);
        }
    }

    /// <summary>Pours the jar out. The contents fly out of the mouth and the sand drains away.</summary>
    public void Empty()
    {
        if (IsEmpty) return;

        if (!IsVisible)
        {
            FinishEmptying();
            return;
        }

        emptying = 0f;
        grabbed = null;
        foreach (var token in tokens)
            token.Vel = new Vector2(Jitter(1.1f), -2.4f - (float)random.NextDouble() * 1.4f);
    }

    private void FinishEmptying()
    {
        emptying = -1f;
        tokens.Clear();
        tokenByKey.Clear();
        layers.Clear();
        layerByKey.Clear();
        grains.Clear();
        drops.Clear();
        sandHeight = 0;
        dropCount = 0;
    }

    private void Bump(float strength)
    {
        flash = Math.Max(flash, strength);
        foreach (var token in tokens)
            token.Vel += new Vector2(Jitter(0.5f), -0.6f - (float)random.NextDouble() * 0.8f) * strength;
        tiltVelocity += Jitter(0.6f) * strength;
    }

    private Vector2 Mouth() => new(0.5f + Jitter(0.05f), NeckTop - 0.06f);

    private float Jitter(float range) => ((float)random.NextDouble() * 2f - 1f) * range;

    // ============================================================================
    // SIMULATION
    // ============================================================================

    private void Simulate(float dt, Vector2 inertia)
    {
        var config = plugin.ConfigService.Configuration;
        if (config.BlacklistedItemIds is { Count: > 0 } blacklist)
            RemoveTokens(t => blacklist.Contains(t.ItemId));

        if (emptying >= 0)
        {
            emptying += dt;
            if (emptying >= EmptySeconds)
            {
                FinishEmptying();
                return;
            }
        }

        UpdatePreview(dt);
        UpdateSand(dt);
        UpdateRadii(dt);

        // The sand sloshes against sideways acceleration and settles on a damped spring.
        tiltVelocity += (-tilt * 60f - tiltVelocity * 7f + inertia.X * 0.12f) * dt;
        tilt = Math.Clamp(tilt + tiltVelocity * dt, -0.14f, 0.14f);

        flash = Math.Max(flash - dt * 1.6f, 0f);

        accumulator = Math.Min(accumulator + dt, Step * MaxSteps);
        while (accumulator >= Step)
        {
            accumulator -= Step;
            StepBodies(Step, inertia);
        }

        UpdateDrops(dt);
        UpdateGrains(dt);
        UpdateSparks(dt);
    }

    private void RemoveTokens(Func<Token, bool> predicate)
    {
        for (var i = tokens.Count - 1; i >= 0; i--)
        {
            var token = tokens[i];
            if (!predicate(token)) continue;
            tokens.RemoveAt(i);
            tokenByKey.Remove((token.ItemId, token.IsHq));
            drops.RemoveAll(d => d.Target == token);
            if (grabbed == token) grabbed = null;
        }
    }

    /// <summary>
    /// Layer thickness follows the log of what has landed: a stack of a million gil should
    /// outweigh a handful of seals, but not bury them. Past <see cref="SandFullWeight"/> the
    /// sand stops rising and the layers share the full height instead.
    /// </summary>
    private void UpdateSand(float dt)
    {
        var drain = emptying >= 0 ? 1f - emptying / EmptySeconds : 1f;

        var total = 0f;
        foreach (var layer in layers)
            total += Weight(layer);

        var scale = SandMax / Math.Max(total, SandFullWeight);
        var ease = snapSand ? 1f : 1f - MathF.Exp(-6f * dt);

        sandHeight = 0;
        foreach (var layer in layers)
        {
            var target = layer.Landed > 0 ? Math.Max(Weight(layer) * scale, MinLayer) : 0f;
            layer.Thickness += (target * drain - layer.Thickness) * ease;
            sandHeight += layer.Thickness;
        }

        snapSand = false;
    }

    private static float Weight(SandLayer layer) => (float)Math.Log10(1 + layer.Landed);

    /// <summary>
    /// Tokens grow with how many of the item you have, and all of them shrink together as the jar
    /// fills, so everything always fits with room left to shake it around.
    /// </summary>
    private void UpdateRadii(float dt)
    {
        if (tokens.Count == 0) return;

        var area = 0f;
        foreach (var token in tokens)
        {
            var f = Factor(token);
            area += MathF.PI * f * f;
        }

        var free = Math.Max(BodyH - sandHeight - 0.18f, 0.2f);
        var unit = Math.Min(MaxTokenRadius, MathF.Sqrt(FillFraction * free / area));
        var ease = 1f - MathF.Exp(-4f * dt);

        foreach (var token in tokens)
            token.Radius += (unit * Factor(token) - token.Radius) * ease;
    }

    private static float Factor(Token token)
        => (1f + 0.25f * MathF.Log10(Math.Max(token.Quantity, 1))) * (1f + 0.05f * (token.Rarity - 1));

    private void StepBodies(float dt, Vector2 inertia)
    {
        var gravity = new Vector2(0, Gravity) - inertia;
        var free = emptying >= 0;

        foreach (var token in tokens)
        {
            if (token == grabbed)
            {
                // Held tokens chase the mouse; whatever speed they have on release is the throw.
                token.Vel = Vector2.Clamp((grabTarget - token.Pos) * 18f, new Vector2(-6f), new Vector2(6f));
            }
            else
            {
                token.Vel += gravity * dt;
                token.Vel *= 1f - 0.6f * dt;
            }

            token.Pos += token.Vel * dt;
            token.Spin += token.Vel.X * dt * 4f / Math.Max(token.Radius * 20f, 0.5f);
            token.Pulse = Math.Max(token.Pulse - dt * 2.2f, 0f);
        }

        if (free) return;

        for (var pass = 0; pass < SolverPasses; pass++)
        {
            for (var i = 0; i < tokens.Count; i++)
            {
                for (var j = i + 1; j < tokens.Count; j++)
                    Collide(tokens[i], tokens[j]);
            }

            foreach (var token in tokens)
                Contain(ref token.Pos, ref token.Vel, token.Radius);
        }
    }

    private void Collide(Token a, Token b)
    {
        var delta = b.Pos - a.Pos;
        var reach = a.Radius + b.Radius;
        var distSq = delta.LengthSquared();
        if (distSq >= reach * reach) return;

        var dist = MathF.Sqrt(distSq);
        var normal = dist > 1e-5f ? delta / dist : new Vector2(1, 0);
        var overlap = reach - dist;

        // Mass goes with area; a token in your hand doesn't give way.
        var invA = a == grabbed ? 0f : 1f / (a.Radius * a.Radius);
        var invB = b == grabbed ? 0f : 1f / (b.Radius * b.Radius);
        var invSum = invA + invB;
        if (invSum <= 0) return;

        a.Pos -= normal * overlap * (invA / invSum);
        b.Pos += normal * overlap * (invB / invSum);

        var closing = Vector2.Dot(b.Vel - a.Vel, normal);
        if (closing >= 0) return;

        var impulse = -(1f + Restitution) * closing / invSum;
        a.Vel -= normal * impulse * invA;
        b.Vel += normal * impulse * invB;

        // A little rolling friction, so piles settle instead of jittering forever.
        var tangent = new Vector2(-normal.Y, normal.X);
        var slide = Vector2.Dot(b.Vel - a.Vel, tangent) * 0.04f;
        a.Vel += tangent * slide * (invA / invSum);
        b.Vel -= tangent * slide * (invB / invSum);
    }

    /// <summary>Keeps a circle inside the glass and on top of the sand.</summary>
    private void Contain(ref Vector2 pos, ref Vector2 vel, float r)
    {
        // Above the shoulders only the neck is open; flung too high, things come back down.
        if (pos.Y < 0)
        {
            Wall(ref pos.X, ref vel.X, NeckL + r, NeckR - r);
            if (pos.Y < NeckTop - 0.55f)
            {
                pos.Y = NeckTop - 0.55f;
                vel.Y = Math.Abs(vel.Y) * Restitution;
            }
            return;
        }

        Wall(ref pos.X, ref vel.X, r, 1f - r);

        // Under the shoulders, outside the neck's opening.
        if (pos.Y < r && (pos.X - r < NeckL || pos.X + r > NeckR))
        {
            pos.Y = r;
            if (vel.Y < 0) vel.Y = -vel.Y * Restitution;
        }

        Corner(ref pos, ref vel, r, new Vector2(ShoulderR, ShoulderR), ShoulderR, pos.X < ShoulderR && pos.Y < ShoulderR);
        Corner(ref pos, ref vel, r, new Vector2(1 - ShoulderR, ShoulderR), ShoulderR, pos.X > 1 - ShoulderR && pos.Y < ShoulderR);
        Corner(ref pos, ref vel, r, new Vector2(BottomR, BodyH - BottomR), BottomR, pos.X < BottomR && pos.Y > BodyH - BottomR);
        Corner(ref pos, ref vel, r, new Vector2(1 - BottomR, BodyH - BottomR), BottomR, pos.X > 1 - BottomR && pos.Y > BodyH - BottomR);

        var floor = Surface(pos.X);
        if (pos.Y + r > floor)
        {
            pos.Y = floor - r;
            if (vel.Y > 0) vel.Y = -vel.Y * Restitution;
            vel.X *= 0.96f;
        }
    }

    private static void Wall(ref float x, ref float v, float min, float max)
    {
        if (x < min) { x = min; if (v < 0) v = -v * Restitution; }
        else if (x > max) { x = max; if (v > 0) v = -v * Restitution; }
    }

    private static void Corner(ref Vector2 pos, ref Vector2 vel, float r, Vector2 center, float radius, bool inCorner)
    {
        if (!inCorner) return;

        var delta = pos - center;
        var dist = delta.Length();
        var limit = Math.Max(radius - r, 0f);
        if (dist <= limit || dist < 1e-5f) return;

        var normal = delta / dist;
        pos = center + normal * limit;
        var outward = Vector2.Dot(vel, normal);
        if (outward > 0) vel -= normal * outward * (1f + Restitution);
    }

    /// <summary>The top of the sand at <paramref name="x"/>, or the bottom of the jar without any.</summary>
    private float Surface(float x) => BodyH - sandHeight + tilt * (x - 0.5f) * Math.Min(sandHeight * 8f, 1f);

    private void UpdateDrops(float dt)
    {
        for (var i = drops.Count - 1; i >= 0; i--)
        {
            var drop = drops[i];
            drop.Age += dt;

            // A repeat drop falls in, then homes in on its token rather than piling up beside it.
            var toTarget = drop.Target.Pos - drop.Pos;
            var dist = toTarget.Length();
            if (drop.Pos.Y > 0.05f && dist > 1e-4f)
                drop.Vel += (toTarget / dist * 4.5f - drop.Vel) * Math.Min(dt * 7f, 1f);
            else
                drop.Vel += new Vector2(0, Gravity) * dt;

            drop.Pos += drop.Vel * dt;

            if (dist < drop.Target.Radius || drop.Age > 2.5f)
            {
                drop.Target.Quantity += drop.Quantity;
                drop.Target.Pulse = 1f;
                drop.Target.Vel += new Vector2(Jitter(0.2f), 0.5f);
                Burst(drop.Target.Pos, Theme.RarityColor(drop.Target.Rarity), 8);
                drops.RemoveAt(i);
            }
        }
    }

    private void UpdateGrains(float dt)
    {
        for (var i = grains.Count - 1; i >= 0; i--)
        {
            var grain = grains[i];
            grain.Vel += new Vector2(0, Gravity * 0.8f) * dt;
            grain.Pos += grain.Vel * dt;

            // A grain that has fallen through the neck is held inside the body walls.
            if (grain.Pos.Y > 0)
                grain.Pos.X = Math.Clamp(grain.Pos.X, 0.02f, 0.98f);

            if (grain.Pos.Y >= Surface(grain.Pos.X))
            {
                grain.Layer.Landed = Math.Min(grain.Layer.Landed + grain.Share, grain.Layer.Amount);
                grains.RemoveAt(i);
                continue;
            }

            grains[i] = grain;
        }
    }

    private void Burst(Vector2 at, Vector4 color, int count)
    {
        var config = plugin.ConfigService.Configuration;
        if (!config.EnableParticleEffects) return;

        count = (int)(count * config.ParticleIntensity);
        for (var i = 0; i < count; i++)
        {
            var angle = (float)(random.NextDouble() * Math.PI * 2);
            var speed = 0.3f + (float)random.NextDouble() * 0.5f;
            var life = 0.4f + (float)random.NextDouble() * 0.4f;
            sparks.Add(new Spark
            {
                Pos = at,
                Vel = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * speed,
                Life = life,
                MaxLife = life,
                Color = color,
            });
        }
    }

    private void UpdateSparks(float dt)
    {
        for (var i = sparks.Count - 1; i >= 0; i--)
        {
            var spark = sparks[i];
            spark.Life -= dt;
            if (spark.Life <= 0)
            {
                sparks.RemoveAt(i);
                continue;
            }

            spark.Vel *= 1f - 2.5f * dt;
            spark.Pos += spark.Vel * dt;
            sparks[i] = spark;
        }
    }

    // ============================================================================
    // DRAWING
    // ============================================================================

    /// <summary>Lays the jar out in the rest of the window's content region and draws it.</summary>
    public void Draw()
    {
        var avail = ImGui.GetContentRegionAvail();
        var origin = ImGui.GetCursorScreenPos();
        var captionH = ImGui.GetTextLineHeight() + Theme.Px(6f);

        // The jar keeps its proportions and takes whichever of width or height runs out first.
        var unitPx = Math.Min(avail.X * 0.62f, (avail.Y - captionH - Theme.Px(8f)) / (BodyH - NeckTop + 0.1f));
        if (unitPx < 40f)
        {
            ImGui.Dummy(avail);
            return;
        }

        var jarH = (BodyH - NeckTop + 0.06f) * unitPx;
        var top = origin.Y + Math.Max((avail.Y - captionH - jarH) * 0.5f, 0f);
        var body = new Vector2(origin.X + (avail.X - unitPx) * 0.5f, top + (-NeckTop + 0.06f) * unitPx);

        var dt = Math.Clamp(ImGui.GetIO().DeltaTime, 0f, 0.05f);
        var inertia = FeelMovement(body / unitPx, dt);

        var hitMin = new Vector2(body.X - Theme.Px(6f), top);
        var hitMax = new Vector2(body.X + unitPx + Theme.Px(6f), body.Y + BodyH * unitPx + Theme.Px(4f));
        ImGui.SetCursorScreenPos(hitMin);
        ImGui.InvisibleButton("##LootJar", hitMax - hitMin, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);

        var mouse = ToJar(ImGui.GetMousePos(), body, unitPx);
        HandleInput(mouse, dt);

        Simulate(dt, inertia);

        var dl = ImGui.GetWindowDrawList();
        Vector2 Px(Vector2 p) => body + p * unitPx;

        DrawGlassBack(dl, body, unitPx);
        DrawSand(dl, body, unitPx);

        foreach (var grain in grains)
        {
            var c = Theme.Lighten(grain.Layer.Color, grain.Shade);
            dl.AddCircleFilled(Px(grain.Pos), Math.Max(unitPx * 0.006f, 1.2f), Theme.U32(c, 0.95f), 6);
        }

        var hovered = ImGui.IsItemHovered() && grabbed == null ? TokenAt(mouse) : null;
        var fade = emptying >= 0 ? 1f - emptying / EmptySeconds : 1f;

        foreach (var token in tokens)
        {
            if (token != grabbed) DrawToken(dl, token, Px(token.Pos), token.Radius * unitPx, token == hovered, fade);
        }

        foreach (var drop in drops)
        {
            var r = drop.Target.Radius * unitPx * 0.6f;
            DrawIcon(dl, drop.Target.IconId, Px(drop.Pos), r, 0.85f);
            dl.AddCircle(Px(drop.Pos), r, Theme.U32(Theme.RarityColor(drop.Target.Rarity), 0.8f), 24, Theme.Px(1.2f));
        }

        // The token in your hand is drawn last, in front of everything else.
        if (grabbed != null)
            DrawToken(dl, grabbed, Px(grabbed.Pos), grabbed.Radius * unitPx * 1.08f, true, 1f);

        foreach (var spark in sparks)
        {
            var t = spark.Life / spark.MaxLife;
            dl.AddCircleFilled(Px(spark.Pos), Math.Max(unitPx * 0.007f * t, 1f), Theme.U32(Theme.Lighten(spark.Color, 0.3f), t), 6);
        }

        DrawGlassFront(dl, body, unitPx);

        if (IsEmpty && emptying < 0)
            DrawEmptyHint(dl, body, unitPx);

        DrawCaption(dl, origin, avail, body.Y + BodyH * unitPx + Theme.Px(6f));
        DrawTooltips(hovered, mouse);
        DrawContextMenu();

        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(avail);
    }

    private static Vector2 ToJar(Vector2 screen, Vector2 body, float unitPx) => (screen - body) / unitPx;

    /// <summary>
    /// The jar is a box moving with the window: when the window accelerates, its contents lag
    /// behind. The result is that acceleration in jar units, applied to the contents as a force.
    /// </summary>
    private Vector2 FeelMovement(Vector2 originUnits, float dt)
    {
        // After a frame without the jar (list view, window closed) the window may be anywhere.
        var frame = ImGui.GetFrameCount();
        if (frame - lastFrame > 1)
        {
            lastOrigin = null;
            originVelocity = Vector2.Zero;
        }
        lastFrame = frame;

        if (dt <= 0) return Vector2.Zero;

        var velocity = lastOrigin is { } last ? (originUnits - last) / dt : Vector2.Zero;
        var acceleration = (velocity - originVelocity) / dt;
        lastOrigin = originUnits;
        originVelocity = velocity;

        // A resize or a teleporting window would otherwise read as a hammer blow.
        return acceleration.Length() > 60f ? Vector2.Normalize(acceleration) * 60f : acceleration;
    }

    private void HandleInput(Vector2 mouse, float dt)
    {
        var hovered = ImGui.IsItemHovered();

        if (ImGui.IsItemActivated() && ImGui.IsMouseDown(ImGuiMouseButton.Left) && emptying < 0)
            grabbed = TokenAt(mouse);

        if (ImGui.IsItemActive() && ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            if (grabbed != null)
                grabTarget = mouse;
        }
        else
        {
            grabbed = null;
        }

        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right) && TokenAt(mouse) is { } token)
        {
            menuToken = token;
            ImGui.OpenPopup("##LootJarMenu");
        }

        if (hovered && (grabbed != null || TokenAt(mouse) != null))
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

    }

    private Token? TokenAt(Vector2 mouse)
    {
        // Back to front, so the token drawn on top wins.
        for (var i = tokens.Count - 1; i >= 0; i--)
        {
            var token = tokens[i];
            if (Vector2.DistanceSquared(token.Pos, mouse) <= token.Radius * token.Radius)
                return token;
        }
        return null;
    }

    /// <summary>The jar's outline: neck, rounded shoulders, straight sides and a rounded base.</summary>
    private static List<Vector2> Outline()
    {
        var points = new List<Vector2> { new(NeckL, NeckTop), new(NeckL, 0) };
        Arc(points, new Vector2(ShoulderR, ShoulderR), ShoulderR, -MathF.PI * 0.5f, -MathF.PI);
        Arc(points, new Vector2(BottomR, BodyH - BottomR), BottomR, MathF.PI, MathF.PI * 0.5f);
        Arc(points, new Vector2(1 - BottomR, BodyH - BottomR), BottomR, MathF.PI * 0.5f, 0);
        Arc(points, new Vector2(1 - ShoulderR, ShoulderR), ShoulderR, 0, -MathF.PI * 0.5f);
        points.Add(new Vector2(NeckR, 0));
        points.Add(new Vector2(NeckR, NeckTop));
        return points;
    }

    /// <summary>The body alone, as a convex polygon the sand layers are cut from.</summary>
    private static readonly List<Vector2> BodyPolygon = BuildBodyPolygon();

    private static List<Vector2> BuildBodyPolygon()
    {
        var points = new List<Vector2>();
        Arc(points, new Vector2(ShoulderR, ShoulderR), ShoulderR, -MathF.PI * 0.5f, -MathF.PI);
        Arc(points, new Vector2(BottomR, BodyH - BottomR), BottomR, MathF.PI, MathF.PI * 0.5f);
        Arc(points, new Vector2(1 - BottomR, BodyH - BottomR), BottomR, MathF.PI * 0.5f, 0);
        Arc(points, new Vector2(1 - ShoulderR, ShoulderR), ShoulderR, 0, -MathF.PI * 0.5f);

        // ImGui's anti-aliased fill wants clockwise points on screen.
        points.Reverse();
        return points;
    }

    private static void Arc(List<Vector2> points, Vector2 center, float radius, float from, float to, int segments = 10)
    {
        for (var i = 0; i <= segments; i++)
        {
            var a = from + (to - from) * i / segments;
            points.Add(center + new Vector2(MathF.Cos(a), MathF.Sin(a)) * radius);
        }
    }

    private void DrawGlassBack(ImDrawListPtr dl, Vector2 body, float unitPx)
    {
        // The inside of the glass, a shade darker than the window behind it.
        dl.PathClear();
        foreach (var p in BodyPolygon) dl.PathLineTo(body + p * unitPx);
        dl.PathFillConvex(Theme.U32(Theme.Ink, 0.55f));

        dl.AddRectFilled(body + new Vector2(NeckL, NeckTop) * unitPx, body + new Vector2(NeckR, 0) * unitPx, Theme.U32(Theme.Ink, 0.55f));

        // A faint glow from below, as if the contents caught the light.
        var glowTop = body + new Vector2(0.08f, BodyH * 0.55f) * unitPx;
        var glowBottom = body + new Vector2(0.92f, BodyH - 0.04f) * unitPx;
        dl.AddRectFilledMultiColor(glowTop, glowBottom,
            Theme.U32(Theme.Crystal, 0f), Theme.U32(Theme.Crystal, 0f),
            Theme.U32(Theme.Crystal, 0.05f), Theme.U32(Theme.Crystal, 0.05f));
    }

    private void DrawSand(ImDrawListPtr dl, Vector2 body, float unitPx)
    {
        if (sandHeight <= 0.0005f) return;

        var fade = emptying >= 0 ? 1f - emptying / EmptySeconds : 1f;
        var tiltFactor = Math.Min(sandHeight * 8f, 1f);

        // Layers are cut from the body between two tilted lines, bottom layer first: the
        // currency you earned first is the bedrock.
        var below = BodyH;
        foreach (var layer in layers)
        {
            if (layer.Thickness <= 0.0005f) continue;

            var above = below - layer.Thickness;
            var poly = ClipBelow(BodyPolygon, above, tilt * tiltFactor);
            poly = ClipAbove(poly, below, tilt * tiltFactor, below >= BodyH - 1e-4f);

            if (poly.Count >= 3)
            {
                dl.PathClear();
                foreach (var p in poly) dl.PathLineTo(body + p * unitPx);
                dl.PathFillConvex(Theme.U32(Theme.Mix(layer.Color, Theme.Ink, 0.25f), 0.95f * fade));
            }

            below = above;
        }

        // The grain: fixed points, each drawn in the colour of the layer it falls in.
        var dot = Math.Max(unitPx * 0.004f, 0.9f);
        foreach (var point in speckle)
        {
            if (point.Y < Surface(point.X) + 0.004f) continue;
            if (LayerAt(point) is not { } layer) continue;

            var shade = (point.X * 7.3f + point.Y * 13.1f) % 1f > 0.5f ? Theme.Lighten(layer.Color, 0.25f) : Theme.Mix(layer.Color, Theme.Ink, 0.5f);
            dl.AddCircleFilled(body + point * unitPx, dot, Theme.U32(shade, 0.7f * fade), 4);
        }

        DrawLayerLabels(dl, body, unitPx, fade);

        // A bright edge where the sand meets the air.
        var surfaceY = Surface(0.5f);
        if (surfaceY < BodyH - BottomR)
        {
            dl.AddLine(body + new Vector2(0.015f, Surface(0.015f)) * unitPx, body + new Vector2(0.985f, Surface(0.985f)) * unitPx,
                Theme.U32(Theme.Lighten(layers[^1].Color, 0.35f), 0.55f * fade), Math.Max(Theme.Px(1.2f), 1f));
        }
    }

    /// <summary>
    /// Each layer's name and amount, centred in the layer. The amount goes first when space runs
    /// short, then the name is shortened, and the text takes ink or light to stand out on its sand.
    /// A layer too thin for its text stays unlabelled; hovering it still names it.
    /// </summary>
    private void DrawLayerLabels(ImDrawListPtr dl, Vector2 body, float unitPx, float fade)
    {
        var font = ImGui.GetFont();
        var size = ImGui.GetFontSize() * LabelScale;
        var ratio = size / ImGui.GetFontSize();
        var maxWidth = unitPx * 0.8f;
        const string separator = "  ";

        var below = BodyH;
        foreach (var layer in layers)
        {
            var above = below - layer.Thickness;
            var middle = (above + below) * 0.5f;
            below = above;

            if (layer.Thickness * unitPx < size + Theme.Px(2f) || string.IsNullOrEmpty(layer.Name)) continue;

            var name = layer.Name;
            var amount = Count((long)layer.Landed);
            float Width(string s) => ImGui.CalcTextSize(s).X * ratio;

            var full = Width(name) + Width(separator) + Width(amount);
            var showAmount = full <= maxWidth;
            if (!showAmount)
            {
                while (name.Length > 1 && Width(name + "...") > maxWidth) name = name[..^1];
                if (name != layer.Name) name += "...";
            }

            var total = showAmount ? full : Width(name);
            var sand = Theme.Mix(layer.Color, Theme.Ink, 0.25f);
            var light = sand.X * 0.299f + sand.Y * 0.587f + sand.Z * 0.114f > 0.5f;
            var ink = light ? Theme.Ink : Theme.Text;

            // At the jar's centre line the tilt cancels out, so the layer's middle is just that.
            var center = body + new Vector2(0.5f, middle) * unitPx;
            var pos = new Vector2(center.X - total * 0.5f, center.Y - size * 0.5f);

            // A soft halo behind the text keeps it readable over the speckle.
            dl.AddText(font, size, pos + new Vector2(0, 1f), Theme.U32(light ? Theme.Text : Theme.Ink, 0.35f * fade), name);
            dl.AddText(font, size, pos, Theme.U32(ink, 0.92f * fade), name);

            if (showAmount)
            {
                var at = pos + new Vector2(Width(name) + Width(separator), 0);
                dl.AddText(font, size, at, Theme.U32(ink, 0.6f * fade), amount);
            }
        }
    }

    /// <summary>The layer at a point in the sand, found by walking up from the bottom.</summary>
    private SandLayer? LayerAt(Vector2 point)
    {
        var tiltFactor = Math.Min(sandHeight * 8f, 1f);
        var depth = BodyH - point.Y + tilt * tiltFactor * (point.X - 0.5f);
        var level = 0f;
        foreach (var layer in layers)
        {
            level += layer.Thickness;
            if (depth <= level) return layer;
        }
        return null;
    }

    /// <summary>Keeps the part of a convex polygon below the line y = level + slope·(x - 0.5).</summary>
    private static List<Vector2> ClipBelow(List<Vector2> poly, float level, float slope)
        => Clip(poly, p => p.Y - (level + slope * (p.X - 0.5f)));

    private static List<Vector2> ClipAbove(List<Vector2> poly, float level, float slope, bool isBottom)
        => isBottom ? poly : Clip(poly, p => level + slope * (p.X - 0.5f) - p.Y);

    /// <summary>Sutherland-Hodgman against one half-plane: keeps the points where <paramref name="side"/> is not negative.</summary>
    private static List<Vector2> Clip(List<Vector2> poly, Func<Vector2, float> side)
    {
        var result = new List<Vector2>(poly.Count + 2);
        for (var i = 0; i < poly.Count; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Count];
            var sa = side(a);
            var sb = side(b);

            if (sa >= 0) result.Add(a);
            if ((sa >= 0) != (sb >= 0))
                result.Add(a + (b - a) * (sa / (sa - sb)));
        }
        return result;
    }

    private void DrawToken(ImDrawListPtr dl, Token token, Vector2 center, float r, bool hovered, float alpha)
    {
        if (r < 1.5f) return;

        var rarity = Theme.RarityColor(token.Rarity);
        var unlock = plugin.ConfigService.Configuration.ShowUnlockStatus ? ItemUnlocks.Get(token.ItemId) : UnlockStatus.NotCollectible;
        var pulse = token.Pulse;
        r *= 1f + 0.16f * pulse * pulse + (hovered ? 0.05f : 0f);

        // A collectible you don't have yet glows, so it can be spotted at the bottom of the pile.
        if (unlock == UnlockStatus.Locked)
        {
            var glow = 0.5f + 0.5f * MathF.Sin(Theme.Time * 3f + token.Spin);
            dl.AddCircleFilled(center, r * 1.35f, Theme.U32(Theme.GoldBright, (0.10f + 0.12f * glow) * alpha), 24);
        }
        else if (token.Rarity >= 3)
        {
            dl.AddCircleFilled(center, r * 1.18f, Theme.U32(rarity, 0.10f * alpha), 24);
        }

        dl.AddCircleFilled(center + new Vector2(r * 0.08f, r * 0.14f), r, Theme.U32(Theme.Ink, 0.5f * alpha), 24);
        dl.AddCircleFilled(center, r, Theme.U32(Theme.Panel, alpha), 24);

        DrawIcon(dl, token.IconId, center, r * 0.84f, alpha);

        dl.AddCircle(center, r, Theme.U32(rarity, (hovered ? 1f : 0.75f) * alpha), 24, Math.Max(Theme.Px(1.4f), 1f));
        if (token.IsHq)
            dl.AddCircle(center, r * 0.9f, Theme.U32(Theme.Warn, 0.7f * alpha), 24, Math.Max(Theme.Px(1f), 1f));
        if (pulse > 0)
            dl.AddCircle(center, r * (1f + (1f - pulse) * 0.5f), Theme.U32(Theme.GoldBright, pulse * alpha), 24, Theme.Px(1.5f));

        // A sheen on the upper left, as on a polished coin.
        dl.AddCircleFilled(center + new Vector2(-r * 0.32f, -r * 0.36f), r * 0.22f, Theme.U32(Theme.Text, 0.08f * alpha), 12);

        if (token.Quantity > 1 && r > Theme.Px(7f))
            DrawCount(dl, center + new Vector2(r * 0.55f, r * 0.62f), Count(token.Quantity), r, alpha);

        if (r > Theme.Px(8f))
            ItemUnlocks.DrawIconSeal(dl, center + new Vector2(r * 0.85f, -r * 0.45f), unlock, Math.Clamp(r / Theme.Px(1f) * 0.55f, 7f, 12f));
    }

    private static void DrawIcon(ImDrawListPtr dl, uint iconId, Vector2 center, float r, float alpha)
    {
        if (iconId == 0) return;
        try
        {
            var tex = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(iconId)).GetWrapOrDefault();
            if (tex != null)
                dl.AddImageRounded(tex.Handle, center - new Vector2(r), center + new Vector2(r), Vector2.Zero, Vector2.One, Theme.U32(new Vector4(1, 1, 1, 1), alpha), r);
        }
        catch { /* Ignore icon loading errors */ }
    }

    /// <summary>The ×N pill on a token, sized to the token so it never swallows the icon.</summary>
    private static void DrawCount(ImDrawListPtr dl, Vector2 anchor, string text, float r, float alpha)
    {
        var font = ImGui.GetFont();
        var size = Math.Clamp(r * 0.62f, Theme.Px(9f), ImGui.GetFontSize());
        var ts = ImGui.CalcTextSize(text) * (size / ImGui.GetFontSize());
        var pad = new Vector2(size * 0.32f, size * 0.08f);
        var min = anchor - ts * 0.5f - pad;
        var max = anchor + ts * 0.5f + pad;

        dl.AddRectFilled(min, max, Theme.U32(Theme.Ink, 0.9f * alpha), (max.Y - min.Y) * 0.5f);
        dl.AddRect(min, max, Theme.U32(Theme.Crystal, 0.45f * alpha), (max.Y - min.Y) * 0.5f, ImDrawFlags.None, 1f);
        dl.AddText(font, size, anchor - ts * 0.5f, Theme.U32(Theme.CrystalBright, alpha), text);
    }

    private static string Count(long n) => n switch
    {
        >= 1_000_000 => $"{n / 1_000_000.0:0.#}m",
        >= 10_000 => $"{n / 1_000}k",
        >= 1_000 => $"{n / 1_000.0:0.#}k",
        _ => n.ToString(),
    };

    private void DrawGlassFront(ImDrawListPtr dl, Vector2 body, float unitPx)
    {
        Vector2 Px(float x, float y) => body + new Vector2(x, y) * unitPx;

        // Reflections: a broad soft band on the left, a thin one on the right.
        dl.AddRectFilledMultiColor(Px(0.06f, 0.22f), Px(0.14f, BodyH - 0.22f),
            Theme.U32(Theme.Text, 0.07f), Theme.U32(Theme.Text, 0.015f),
            Theme.U32(Theme.Text, 0.015f), Theme.U32(Theme.Text, 0.05f));
        dl.AddRectFilled(Px(0.88f, 0.3f), Px(0.9f, BodyH * 0.6f), Theme.U32(Theme.Text, 0.06f), unitPx * 0.01f);

        var outline = Outline();
        var edge = Theme.Mix(Theme.Line, Theme.CrystalDim, 0.5f + 0.5f * flash);
        dl.PathClear();
        foreach (var p in outline) dl.PathLineTo(body + p * unitPx);
        dl.PathStroke(Theme.U32(edge, 0.95f), ImDrawFlags.None, Math.Max(Theme.Px(2f), 1.5f));

        dl.PathClear();
        foreach (var p in outline) dl.PathLineTo(body + (p + (new Vector2(0.5f, BodyH * 0.5f) - p) * 0.02f) * unitPx);
        dl.PathStroke(Theme.U32(Theme.Text, 0.06f + 0.2f * flash), ImDrawFlags.None, 1f);

        // The brass rim around the mouth.
        var rimMin = Px(NeckL - 0.035f, NeckTop - 0.045f);
        var rimMax = Px(NeckR + 0.035f, NeckTop + 0.025f);
        dl.AddRectFilled(rimMin, rimMax, Theme.U32(Theme.GoldDim, 0.95f), unitPx * 0.02f);
        dl.AddRectFilledMultiColor(rimMin, new Vector2(rimMax.X, (rimMin.Y + rimMax.Y) * 0.5f),
            Theme.U32(Theme.GoldBright, 0.35f), Theme.U32(Theme.GoldBright, 0.2f),
            Theme.U32(Theme.Gold, 0f), Theme.U32(Theme.Gold, 0f));
        dl.AddRect(rimMin, rimMax, Theme.U32(Theme.Gold, 0.9f), unitPx * 0.02f, ImDrawFlags.None, 1f);
    }

    private static void DrawEmptyHint(ImDrawListPtr dl, Vector2 body, float unitPx)
    {
        using var s = new Theme.FontScale(0.9f);
        const string text = "Your drops fall in here";
        var ts = ImGui.CalcTextSize(text);
        if (ts.X > unitPx * 0.9f) return;

        var at = body + new Vector2(0.5f, BodyH * 0.55f) * unitPx - ts * 0.5f;
        dl.AddText(at, Theme.U32(Theme.TextFaint, 0.8f), text);
    }

    private void DrawCaption(ImDrawListPtr dl, Vector2 origin, Vector2 avail, float y)
    {
        if (IsEmpty || emptying >= 0) return;

        using var s = new Theme.FontScale(0.85f);
        var parts = new List<string>();
        if (tokens.Count > 0) parts.Add(tokens.Count == 1 ? "1 item" : $"{tokens.Count} items");
        parts.Add(dropCount == 1 ? "1 drop" : $"{dropCount} drops");

        var text = string.Join("  ·  ", parts);
        var ts = ImGui.CalcTextSize(text);
        if (ts.X > avail.X) return;
        dl.AddText(new Vector2(origin.X + (avail.X - ts.X) * 0.5f, y), Theme.U32(Theme.TextFaint), text);
    }

    private void DrawTooltips(Token? hovered, Vector2 mouse)
    {
        if (!ImGui.IsItemHovered() || ImGui.IsItemActive() || ImGui.IsPopupOpen("##LootJarMenu")) return;

        if (hovered != null)
        {
            if (!plugin.ConfigService.Configuration.ShowTooltips) return;
            var token = hovered;
            ItemTooltip.Show(token.ItemId, token.IsHq, () =>
            {
                ItemTooltip.Row(FontAwesomeIcon.LayerGroup, "In the jar", $"x{token.Quantity:N0}");
                ItemTooltip.Row(FontAwesomeIcon.Redo, "Drops", token.Drops.ToString());
                ItemTooltip.MarketRow(plugin, token.ItemId, token.IsHq);
                ImGui.TextColored(Theme.TextFaint, $"Last one {token.LastAt:HH:mm:ss}");
            }, "Drag to throw it · right-click for Try On and more", plugin.ConfigService.Configuration.ShowUnlockStatus ? ItemUnlocks.Get(token.ItemId) : UnlockStatus.NotCollectible);
            return;
        }

        if (mouse.Y >= Surface(mouse.X) && mouse.X is >= 0 and <= 1 && LayerAt(mouse) is { } layer)
        {
            var lines = new List<string> { layer.Name, $"+{layer.Amount:N0} this session" };
            if (layer.Parts.Count > 1)
            {
                foreach (var (itemId, amount) in layer.Parts.OrderBy(p => p.Key))
                    lines.Add($"   {ItemInfo(itemId).Name}  x{amount:N0}");
            }
            lines.Add($"Last at {layer.LastAt:HH:mm:ss}");
            Theme.Tooltip(string.Join("\n", lines));
            return;
        }

        // The controls aren't obvious, so the glass explains them once you linger on it.
    }

    private void DrawContextMenu()
    {
        using var popup = ImRaii.Popup("##LootJarMenu");
        if (!popup || menuToken is not { } token) return;
        Theme.SetFontScale(1f);

        ImGui.TextColored(Theme.RarityColor(token.Rarity), token.Name);
        ImGui.Separator();

        Theme.Icon(FontAwesomeIcon.Ban, Theme.Bad);
        ImGui.SameLine(0, Theme.Px(8));
        if (ImGui.MenuItem("Add to blacklist"))
        {
            var config = plugin.ConfigService.Configuration;
            config.BlacklistedItemIds ??= new List<uint>();
            if (!config.BlacklistedItemIds.Contains(token.ItemId))
            {
                config.BlacklistedItemIds.Add(token.ItemId);
                plugin.ConfigService.Save();
            }
        }

        ImGui.Separator();
        ItemActions.DrawMenuItems(token.ItemId, token.Name, plugin.MarketPriceService.IsMarketable(token.ItemId));
    }
}
