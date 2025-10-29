using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace LootView.Windows;

/// <summary>
/// Base window class with ImGui support
/// </summary>
public abstract class Window : IDisposable
{
    protected string WindowName { get; }
    
    private bool isOpen = false;
    
    public bool IsOpen
    {
        get => isOpen;
        set => isOpen = value;
    }

    protected ImGuiWindowFlags WindowFlags { get; set; } = ImGuiWindowFlags.None;
    protected Vector2? Size { get; set; } = null;
    protected Vector2? SizeConstraintMin { get; set; } = null;
    protected Vector2? SizeConstraintMax { get; set; } = null;

    protected Window(string name)
    {
        WindowName = name;
    }

    public void Draw()
    {
        if (!IsOpen) return;

        try
        {
            // Set size constraints if specified
            if (SizeConstraintMin.HasValue && SizeConstraintMax.HasValue)
            {
                ImGui.SetNextWindowSizeConstraints(SizeConstraintMin.Value, SizeConstraintMax.Value);
            }
            
            // Set initial size if specified
            if (Size.HasValue)
            {
                ImGui.SetNextWindowSize(Size.Value, ImGuiCond.FirstUseEver);
            }

            // Begin the ImGui window
            if (ImGui.Begin(WindowName, ref isOpen, WindowFlags))
            {
                DrawContents();
            }
            ImGui.End();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Error drawing window {WindowName}", WindowName);
        }
    }

    protected abstract void DrawContents();

    public virtual void Dispose()
    {
        // Override in derived classes if needed
    }
}