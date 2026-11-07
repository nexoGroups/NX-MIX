namespace NxMix.App.Ui;

/// <summary>Kurzer UI-Übergang, der bei einem neuen Ziel vom aktuellen Wert ausgeht.</summary>
internal struct Motion
{
    public const float Feedback = 0.12f;
    public const float Selection = 0.16f;
    public const float Reveal = 0.18f;

    bool _initialized;
    double _start;
    float _from, _to;

    public void Start(float from, float to, double now)
    {
        _initialized = true;
        _start = now;
        _from = from;
        _to = to;
    }

    public float At(UiRoot? root, float target, float seconds)
    {
        double now = root?.Time ?? 0;
        if (!_initialized || root == null || root.ReduceMotion)
        {
            Start(target, target, now);
            return target;
        }

        float value = Sample(now, seconds);
        if (target != _to)
        {
            Start(value, target, now);
        }
        if (value != target) root.Invalidate();
        return value;
    }

    float Sample(double now, float seconds)
    {
        float remaining = 1 - Math.Clamp((float)((now - _start) / seconds), 0, 1);
        return _to + (_from - _to) * remaining * remaining * remaining;
    }
}
