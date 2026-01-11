using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace CeraRegularize.Stores
{
    public sealed class ConfigState
    {
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public bool RememberMe { get; set; }
        public bool AutoLogin { get; set; }
        public string Shift { get; set; } = "S02";
        public string InTime { get; set; } = "0900";
        public string OutTime { get; set; } = "1800";
        public string WfoRemarks { get; set; } = "Working from Office";
        public string WfhRemarks { get; set; } = "Working from Home";
        public bool LoginVerified { get; set; }
        public string LoginState { get; set; } = "login_not_verified";
        public string LoginMessage { get; set; } = "Session Inactive";

        public ConfigState Copy()
        {
            return new ConfigState
            {
                Username = Username,
                Password = Password,
                RememberMe = RememberMe,
                AutoLogin = AutoLogin,
                Shift = Shift,
                InTime = InTime,
                OutTime = OutTime,
                WfoRemarks = WfoRemarks,
                WfhRemarks = WfhRemarks,
                LoginVerified = LoginVerified,
                LoginState = LoginState,
                LoginMessage = LoginMessage,
            };
        }

        public bool ContentEquals(ConfigState? other)
        {
            if (other is null)
            {
                return false;
            }

            return string.Equals(Username, other.Username, StringComparison.Ordinal)
                && string.Equals(Password, other.Password, StringComparison.Ordinal)
                && RememberMe == other.RememberMe
                && AutoLogin == other.AutoLogin
                && string.Equals(Shift, other.Shift, StringComparison.Ordinal)
                && string.Equals(InTime, other.InTime, StringComparison.Ordinal)
                && string.Equals(OutTime, other.OutTime, StringComparison.Ordinal)
                && string.Equals(WfoRemarks, other.WfoRemarks, StringComparison.Ordinal)
                && string.Equals(WfhRemarks, other.WfhRemarks, StringComparison.Ordinal)
                && LoginVerified == other.LoginVerified
                && string.Equals(LoginState, other.LoginState, StringComparison.Ordinal)
                && string.Equals(LoginMessage, other.LoginMessage, StringComparison.Ordinal);
        }
    }

    public static class ConfigStore
    {
        private const string StoreFileName = "config_store.json";
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
        };

        public static readonly Dictionary<string, (string InTime, string OutTime)> ShiftDefaults =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["S01"] = ("0800", "2000"),
                ["S03"] = ("2000", "0800"),
                ["S02"] = ("0900", "1800"),
                ["GEN"] = ("0900", "1800"),
            };

        public static ConfigState DefaultConfigState()
        {
            return new ConfigState
            {
                Username = string.Empty,
                Password = string.Empty,
                RememberMe = false,
                AutoLogin = false,
                Shift = "S02",
                InTime = ShiftDefaults["S02"].InTime,
                OutTime = ShiftDefaults["S02"].OutTime,
                WfoRemarks = "Working from Office",
                WfhRemarks = "Working from Home",
                LoginVerified = false,
                LoginState = "login_not_verified",
                LoginMessage = "Session Inactive",
            };
        }

        public static ConfigState Load()
        {
            var state = DefaultConfigState();
            var path = AppPaths.DataFile(StoreFileName);
            if (!File.Exists(path))
            {
                return state;
            }

            try
            {
                var raw = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<ConfigState>(raw);
                if (loaded != null)
                {
                    state = Merge(state, loaded);
                }
            }
            catch
            {
                return state;
            }

            return Normalize(state);
        }

        public static ConfigState Save(ConfigState data)
        {
            var snapshot = Normalize(data);
            var path = AppPaths.DataFile(StoreFileName);
            try
            {
                var json = JsonSerializer.Serialize(snapshot, JsonOptions);
                File.WriteAllText(path, json);
            }
            catch
            {
            }

            return snapshot;
        }

        private static ConfigState Merge(ConfigState baseState, ConfigState other)
        {
            return new ConfigState
            {
                Username = string.IsNullOrWhiteSpace(other.Username) ? baseState.Username : other.Username,
                Password = string.IsNullOrWhiteSpace(other.Password) ? baseState.Password : other.Password,
                RememberMe = other.RememberMe,
                AutoLogin = other.AutoLogin,
                Shift = string.IsNullOrWhiteSpace(other.Shift) ? baseState.Shift : other.Shift,
                InTime = string.IsNullOrWhiteSpace(other.InTime) ? baseState.InTime : other.InTime,
                OutTime = string.IsNullOrWhiteSpace(other.OutTime) ? baseState.OutTime : other.OutTime,
                WfoRemarks = string.IsNullOrWhiteSpace(other.WfoRemarks) ? baseState.WfoRemarks : other.WfoRemarks,
                WfhRemarks = string.IsNullOrWhiteSpace(other.WfhRemarks) ? baseState.WfhRemarks : other.WfhRemarks,
                LoginVerified = other.LoginVerified,
                LoginState = string.IsNullOrWhiteSpace(other.LoginState) ? baseState.LoginState : other.LoginState,
                LoginMessage = string.IsNullOrWhiteSpace(other.LoginMessage) ? baseState.LoginMessage : other.LoginMessage,
            };
        }

        private static ConfigState Normalize(ConfigState state)
        {
            var shift = string.IsNullOrWhiteSpace(state.Shift) ? "S02" : state.Shift;
            if (!ShiftDefaults.ContainsKey(shift))
            {
                shift = "S02";
            }

            var defaults = ShiftDefaults[shift];
            return new ConfigState
            {
                Username = state.Username ?? string.Empty,
                Password = state.Password ?? string.Empty,
                RememberMe = state.RememberMe,
                AutoLogin = state.AutoLogin,
                Shift = shift,
                InTime = string.IsNullOrWhiteSpace(state.InTime) ? defaults.InTime : state.InTime,
                OutTime = string.IsNullOrWhiteSpace(state.OutTime) ? defaults.OutTime : state.OutTime,
                WfoRemarks = string.IsNullOrWhiteSpace(state.WfoRemarks) ? "Working from Office" : state.WfoRemarks,
                WfhRemarks = string.IsNullOrWhiteSpace(state.WfhRemarks) ? "Working from Home" : state.WfhRemarks,
                LoginVerified = state.LoginVerified,
                LoginState = string.IsNullOrWhiteSpace(state.LoginState) ? "login_not_verified" : state.LoginState,
                LoginMessage = string.IsNullOrWhiteSpace(state.LoginMessage) ? "Session Inactive" : state.LoginMessage,
            };
        }
    }
}
