namespace MyApp.Models.Chat
{
    public enum ChatRole
    {
        User,
        Assistant,
        Error,
    }

    public sealed class ChatMessage
    {
        public ChatRole Role { get; set; }

        public string Text { get; set; } = string.Empty;

        public DateTimeOffset Timestamp { get; set; }
    }
}
