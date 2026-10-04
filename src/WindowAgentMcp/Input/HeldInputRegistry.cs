namespace WindowAgentMcp.Input;

public sealed class HeldInputRegistry
{
    public static readonly TimeSpan HoldTimeout = TimeSpan.FromSeconds(60);

    private readonly object _sync = new();
    private readonly Dictionary<KeyIdentity, DateTimeOffset> _keys = [];
    private readonly Dictionary<MouseButton, DateTimeOffset> _buttons = [];

    public bool ContainsKey(KeyIdentity key)
    {
        lock (_sync)
        {
            return _keys.ContainsKey(key);
        }
    }

    public bool ContainsButton(MouseButton button)
    {
        lock (_sync)
        {
            return _buttons.ContainsKey(button);
        }
    }

    public void PressKey(KeyIdentity key) => SetExpiration(_keys, key);

    public void ReleaseKey(KeyIdentity key)
    {
        lock (_sync)
        {
            _keys.Remove(key);
        }
    }

    public void PressButton(MouseButton button) => SetExpiration(_buttons, button);

    public void ReleaseButton(MouseButton button)
    {
        lock (_sync)
        {
            _buttons.Remove(button);
        }
    }

    public HeldInputSnapshot TakeExpired(DateTimeOffset now)
    {
        lock (_sync)
        {
            var keys = _keys.Where(entry => entry.Value <= now).Select(entry => entry.Key).ToArray();
            var buttons = _buttons.Where(entry => entry.Value <= now).Select(entry => entry.Key).ToArray();
            foreach (var key in keys)
            {
                _keys.Remove(key);
            }

            foreach (var button in buttons)
            {
                _buttons.Remove(button);
            }

            return new HeldInputSnapshot(keys, buttons);
        }
    }

    public HeldInputSnapshot TakeAll()
    {
        lock (_sync)
        {
            var snapshot = new HeldInputSnapshot(_keys.Keys.ToArray(), _buttons.Keys.ToArray());
            _keys.Clear();
            _buttons.Clear();
            return snapshot;
        }
    }

    public HeldInputSnapshot Snapshot()
    {
        lock (_sync)
        {
            return new HeldInputSnapshot(_keys.Keys.ToArray(), _buttons.Keys.ToArray());
        }
    }

    private void SetExpiration<TKey>(Dictionary<TKey, DateTimeOffset> states, TKey key) where TKey : notnull
    {
        lock (_sync)
        {
            states[key] = DateTimeOffset.UtcNow + HoldTimeout;
        }
    }
}
