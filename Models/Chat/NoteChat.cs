using System.Text.Json.Serialization;

namespace MyApp.Models.Chat
{
    public sealed class NoteChat
    {
        public int Version { get; set; } = 1;

        public string? NotePath { get; set; }

        public string? LastProvider { get; set; }

        public Dictionary<string, List<ChatMessage>> Conversations { get; set; } = new();

        public List<ChatMessage> ConversationFor(string providerId)
        {
            if (!Conversations.TryGetValue(providerId, out List<ChatMessage>? conversation))
            {
                conversation = new List<ChatMessage>();
                Conversations[providerId] = conversation;
            }
            return conversation;
        }

        [JsonIgnore]
        public bool HasMessages => Conversations.Values.Any(c => c.Count > 0);
    }
}
