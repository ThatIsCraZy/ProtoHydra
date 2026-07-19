using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataService.Core.Authentication;

namespace DataService.App.ViewModels;

public sealed partial class AuthenticationViewModel : ObservableObject
{
    private readonly RuntimeAuthenticationPolicy _runtimePolicy;
    private readonly AuthenticationSettingsStore _store;
    private readonly List<UserAccount> _users;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private bool _isDefinedUsersMode;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddUserCommand))]
    private string _newUsername = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddUserCommand))]
    private string _newPassword = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddUserCommand))]
    private string _newPasswordConfirm = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveUserCommand))]
    private string? _selectedUser;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private bool _hasUnappliedChanges;

    public AuthenticationViewModel(RuntimeAuthenticationPolicy runtimePolicy, AuthenticationSettingsStore store)
    {
        _runtimePolicy = runtimePolicy;
        _store = store;

        var settings = store.Load();
        _users = settings.Users.ToList();
        _isDefinedUsersMode = settings.Mode == AuthenticationMode.DefinedUsers;
        Usernames = new ObservableCollection<string>(_users.Select(user => user.Username));

        AddUserCommand = new RelayCommand(AddUser, CanAddUser);
        RemoveUserCommand = new RelayCommand(RemoveUser, () => SelectedUser is not null);
        ApplyCommand = new RelayCommand(Apply, CanApply);
    }

    public ObservableCollection<string> Usernames { get; }

    public RelayCommand AddUserCommand { get; }

    public RelayCommand RemoveUserCommand { get; }

    public RelayCommand ApplyCommand { get; }

    public event EventHandler? Applied;

    partial void OnIsDefinedUsersModeChanged(bool value) => MarkDirty();

    partial void OnSelectedUserChanged(string? value) => RemoveUserCommand.NotifyCanExecuteChanged();

    private bool CanAddUser()
        => !string.IsNullOrWhiteSpace(NewUsername)
            && NewPassword.Length > 0
            && NewPassword == NewPasswordConfirm;

    private void AddUser()
    {
        var username = NewUsername.Trim();
        var existingIndex = _users.FindIndex(
            user => string.Equals(user.Username, username, StringComparison.OrdinalIgnoreCase));
        var account = new UserAccount(username, Argon2PasswordHasher.Hash(NewPassword));

        if (existingIndex >= 0)
        {
            _users[existingIndex] = account;
            StatusText = $"Password for '{username}' updated (apply to activate).";
        }
        else
        {
            _users.Add(account);
            Usernames.Add(username);
            StatusText = $"User '{username}' added (apply to activate).";
        }

        NewUsername = "";
        NewPassword = "";
        NewPasswordConfirm = "";
        MarkDirty();
    }

    private void RemoveUser()
    {
        if (SelectedUser is not { } username)
        {
            return;
        }

        _users.RemoveAll(user => string.Equals(user.Username, username, StringComparison.OrdinalIgnoreCase));
        Usernames.Remove(username);
        SelectedUser = null;
        StatusText = $"User '{username}' removed (apply to activate).";
        MarkDirty();
    }

    private bool CanApply()
        => !IsDefinedUsersMode || _users.Count > 0;

    private void Apply()
    {
        var settings = new AuthenticationSettings(
            IsDefinedUsersMode ? AuthenticationMode.DefinedUsers : AuthenticationMode.AcceptAny,
            _users.ToArray());
        _store.Save(settings);
        _runtimePolicy.Replace(AuthenticationSettingsStore.CreatePolicy(settings));
        HasUnappliedChanges = false;
        StatusText = IsDefinedUsersMode
            ? $"Applied: {_users.Count} defined user(s) required for FTP/FTPS, SFTP, SCP, HTTP and HTTPS."
            : "Applied: Accept-Any policy active (no access control).";
        Applied?.Invoke(this, EventArgs.Empty);
    }

    private void MarkDirty()
    {
        HasUnappliedChanges = true;
        ApplyCommand.NotifyCanExecuteChanged();
    }
}
