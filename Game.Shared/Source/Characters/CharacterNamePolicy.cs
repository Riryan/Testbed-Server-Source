using System.Text.RegularExpressions;

namespace Game.Shared.Characters
{
    /// <summary>
    /// Portable character-name policy matching the current production uMMORPG rule:
    /// 1-16 ASCII letters, with optional single spaces between words.
    /// </summary>
    public static class CharacterNamePolicy
    {
        public const int MaxLength = 16;
        private static readonly Regex Allowed = new Regex(@"^[a-zA-Z]+(?: [a-zA-Z]+)*$", RegexOptions.CultureInvariant);

        public static string Normalize(string name) =>
            string.IsNullOrWhiteSpace(name) ? string.Empty : name.Trim();

        public static bool IsAllowed(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > MaxLength)
                return false;
            if (name != name.Trim())
                return false;
            return Allowed.IsMatch(name);
        }
    }
}
