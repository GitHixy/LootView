using System;
using System.Numerics;

namespace LootView.Models;

/// <summary>
/// Represents a visual particle effect for loot items
/// </summary>
public class ParticleEffect
{
    public Vector2 Position { get; set; }
    public Vector2 Velocity { get; set; }
    public Vector4 Color { get; set; }
    public float Size { get; set; }
    public float Life { get; set; }
    public float MaxLife { get; set; }
    public ParticleType Type { get; set; }
    public float Rotation { get; set; }
    public float RotationSpeed { get; set; }
    
    public bool IsAlive => Life > 0;
    
    /// <summary>
    /// Update particle state
    /// </summary>
    public void Update(float deltaTime)
    {
        Life -= deltaTime;
        Position += Velocity * deltaTime;
        Rotation += RotationSpeed * deltaTime;
        
        // Apply gravity for certain particle types
        if (Type == ParticleType.Spark || Type == ParticleType.Star)
        {
            Velocity = new Vector2(Velocity.X, Velocity.Y + 50f * deltaTime); // Gravity
        }
        
        // Fade out over time
        var alpha = Life / MaxLife;
        Color = new Vector4(Color.X, Color.Y, Color.Z, alpha * 0.8f);
    }
}

/// <summary>
/// Types of particle effects
/// </summary>
public enum ParticleType
{
    Spark,      // Small bright sparks
    Glow,       // Soft glowing orbs
    Star,       // Star-shaped particles
    Ring,       // Expanding rings
    Trail,      // Motion trail
    Shimmer     // Twinkling effect
}
