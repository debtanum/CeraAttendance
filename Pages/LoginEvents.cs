using System;

namespace CeraRegularize.Pages
{
    public sealed class LoginRequestedEventArgs : EventArgs
    {
        public LoginRequestedEventArgs(string username, string password, bool remember)
        {
            Username = username;
            Password = password;
            Remember = remember;
        }

        public string Username { get; }
        public string Password { get; }
        public bool Remember { get; }
    }
}
