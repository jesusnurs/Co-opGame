using System;

public static class GameplayTextInputBlocker
{
    public static event Action<bool> OnTypingStateChanged;

    public static bool IsTyping { get; private set; }

    public static void SetTyping(bool isTyping)
    {
        if (IsTyping == isTyping)
        {
            return;
        }

        IsTyping = isTyping;
        OnTypingStateChanged?.Invoke(IsTyping);
    }
}
