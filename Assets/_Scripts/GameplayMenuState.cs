using System;

public static class GameplayMenuState
{
    public static event Action<bool> OnMenuStateChanged;

    public static bool IsMenuOpen { get; private set; } = true;

    public static void SetMenuOpen(bool isOpen)
    {
        if (IsMenuOpen == isOpen)
        {
            return;
        }

        IsMenuOpen = isOpen;
        OnMenuStateChanged?.Invoke(IsMenuOpen);
    }
}
