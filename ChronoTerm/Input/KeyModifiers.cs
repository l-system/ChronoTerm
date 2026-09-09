namespace ChronoTerm.Input;

[Flags]
public enum KeyModifiers
{
    None = 0,
    Control = 1 << 0,
    Alt = 1 << 1,
    Shift = 1 << 2,
}
