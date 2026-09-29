using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using MyApp.Models;
using MyApp.Models.Chat;
using MyApp.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MyApp.ViewModels
{
    public sealed class ChatMessageItem
    {
        public ChatMessageItem(ChatMessage message)
        {
            Text = message.Text;
            bool isUser = message.Role == ChatRole.User;
            Alignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            Bubble = Brush(isUser ? "NoteAccentBrush" : "NoteMenuBrush");
            Foreground = Brush(message.Role switch
            {
                ChatRole.User => "NoteAccentTextBrush",
                ChatRole.Error => "NoteStrikeMarkupBrush",
                _ => "NoteTextBrush",
            });
        }

        public string Text { get; }

        public HorizontalAlignment Alignment { get; }

        public Brush Bubble { get; }

        public Brush Foreground { get; }

        private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
    }

    public sealed partial class ChatViewModel : ObservableObject
    {
        private readonly ConditionalWeakTable<NoteTab, NoteChat> _chats = new();
        private NoteTab? _tab;
        private bool _switchingNote;

        public ChatViewModel()
        {
            Messages.CollectionChanged += (s, e) => OnPropertyChanged(nameof(HasNoMessages));
        }

        public IReadOnlyList<IAiProvider> Providers => AiProviders.All;

        public ObservableCollection<ChatMessageItem> Messages { get; } = new();

        public bool HasNoMessages => Messages.Count == 0;

        [ObservableProperty]
        public partial IAiProvider SelectedProvider { get; set; } = AiProviders.All[0];

        [ObservableProperty]
        public partial bool IsBusy { get; set; }

        partial void OnSelectedProviderChanged(IAiProvider value)
        {
            if (_switchingNote || _tab is null) return;

            NoteChat chat = ChatFor(_tab);
            chat.LastProvider = value.Id;
            Persist(_tab, chat);
            ShowConversation();
        }

        public void SetNote(NoteTab? tab)
        {
            if (_tab is not null) _tab.PropertyChanged -= Tab_PropertyChanged;
            _tab = tab;

            if (tab is null)
            {
                Messages.Clear();
                return;
            }

            tab.PropertyChanged += Tab_PropertyChanged;
            _switchingNote = true;
            SelectedProvider = AiProviders.Find(ChatFor(tab).LastProvider);
            _switchingNote = false;
            ShowConversation();
        }

        public async Task SendAsync(string prompt)
        {
            if (_tab is not NoteTab tab || IsBusy) return;

            IAiProvider provider = SelectedProvider;
            NoteChat chat = ChatFor(tab);
            List<ChatMessage> conversation = chat.ConversationFor(provider.Id);
            chat.LastProvider = provider.Id;

            Add(tab, provider, conversation, new ChatMessage { Role = ChatRole.User, Text = prompt, Timestamp = DateTimeOffset.Now });
            Persist(tab, chat);

            IsBusy = true;
            ChatMessage reply;
            try
            {
                string answer = await provider.SendAsync(conversation, prompt, CancellationToken.None);
                reply = new ChatMessage { Role = ChatRole.Assistant, Text = answer, Timestamp = DateTimeOffset.Now };
            }
            catch (Exception ex)
            {
                reply = new ChatMessage { Role = ChatRole.Error, Text = ex.Message, Timestamp = DateTimeOffset.Now };
            }
            finally
            {
                IsBusy = false;
            }

            Add(tab, provider, conversation, reply);
            Persist(tab, chat);
        }

        private void Add(NoteTab tab, IAiProvider provider, List<ChatMessage> conversation, ChatMessage message)
        {
            conversation.Add(message);
            if (ReferenceEquals(tab, _tab) && ReferenceEquals(provider, SelectedProvider)) Messages.Add(new ChatMessageItem(message));
        }

        private void ShowConversation()
        {
            Messages.Clear();
            if (_tab is null) return;

            foreach (ChatMessage message in ChatFor(_tab).ConversationFor(SelectedProvider.Id))
            {
                Messages.Add(new ChatMessageItem(message));
            }
        }

        private NoteChat ChatFor(NoteTab tab) =>
            _chats.GetValue(tab, t => t.FilePath is null ? new NoteChat() : ChatStore.Load(t.FilePath));

        private static void Persist(NoteTab tab, NoteChat chat)
        {
            if (tab.FilePath is not null && chat.HasMessages) ChatStore.Save(tab.FilePath, chat);
        }

        private void Tab_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(NoteTab.FilePath) || sender is not NoteTab tab) return;

            NoteChat chat = ChatFor(tab);
            if (chat.HasMessages)
            {
                Persist(tab, chat);
                return;
            }

            _chats.Remove(tab);
            if (ReferenceEquals(tab, _tab)) SetNote(tab);
        }
    }
}
