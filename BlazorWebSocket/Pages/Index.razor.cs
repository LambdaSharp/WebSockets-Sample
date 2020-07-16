/*
 * LambdaSharp (λ#)
 * Copyright (C) 2018-2020
 * lambdasharp.net
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Blazorise;
using Blazorise.Sidebar;
using BlazorWebSocket.Common;
using LambdaSharp.Chat.Common.Notifications;
using LambdaSharp.Chat.Common.Records;
using LambdaSharp.Chat.Common.Requests;
using Microsoft.AspNetCore.Components;

namespace BlazorWebSocket.Pages {

    public class IndexBase : ComponentWithLocalStorageBase, IDisposable {

        //--- Constants ---
        private const int TOKEN_EXPIRATION_LIMIT_SECONDS = 5 * 60;

        //--- Types ---
        protected enum ConnectionState {
            Initializing,
            Unauthorized,
            Connecting,
            Connected
        }
        Sidebar sidebar;
        
        SidebarInfo sidebarInfo = new SidebarInfo {
            Brand = new SidebarBrandInfo {
                Text = "LambdaSharp Chat"
            },
            Items = new List<SidebarItemInfo> {
                new SidebarItemInfo {
                    Text = "Chats",
                    Icon = IconName.Mail,
                    SubItems = new List<SidebarItemInfo>()
                }
            }
        };

        //--- Properties ---
        protected List<UserMessageNotification> Messages { get; set; } = new List<UserMessageNotification>();
        protected string ChatMessage { get; set; }
        protected string UserId { get; set; }
        protected string UserName { get; set; }
        protected ConnectionState State { get; set; } = ConnectionState.Initializing;
        protected WebSocketDispatcher WebSocketDispatcher { get; set; }
        protected string LoginUrl;
        // protected List<ChannelRecord> UserChannelList {get; set;}
        [Inject] private HttpClient HttpClient { get; set; }
        [Inject] private CognitoSettings CognitoSettings { get; set; }

        //--- Methods ---
        protected override async Task OnInitializedAsync() {

            // configure WebSocket dispatcher
            WebSocketDispatcher = new WebSocketDispatcher(new Uri($"wss://{HttpClient.BaseAddress.Host}/socket"));
            WebSocketDispatcher.RegisterAction<UserMessageNotification>("message", ReceivedMessage);
            WebSocketDispatcher.RegisterAction<UserNameChangedNotification>("username", ReceivedUserNameChanged);
            WebSocketDispatcher.RegisterAction<WelcomeNotification>("welcome", ReceivedWelcomeAsync);
            WebSocketDispatcher.RegisterAction<JoinedChannelNotification>("joined", ReceivedJoinedChannel);

            // attempt to restore authentication tokens from local storage
            var authenticationTokens = await GetAuthenticationTokens();
            if(authenticationTokens == null) {
                var guard = await CreateReplayGuardAsync();
                LoginUrl = CognitoSettings.GetLoginUrl(guard);
                Console.WriteLine($"Login URL: {LoginUrl}");
                State = ConnectionState.Unauthorized;
            } else {
                Console.WriteLine("Attempting to connect to websocket");
                State = ConnectionState.Connecting;

                // attempt to connect to the websocket
                WebSocketDispatcher.IdToken = authenticationTokens.IdToken;
                if(await WebSocketDispatcher.Connect()) {
                    Console.WriteLine("Websocket connection succeeded");
                    await WebSocketDispatcher.SendMessageAsync(new HelloRequest());
                } else {
                    Console.WriteLine("Websocket connection failed");
                    await ClearAuthenticationTokens();
                    var guard = await CreateReplayGuardAsync();
                    LoginUrl = CognitoSettings.GetLoginUrl(guard);
                    Console.WriteLine($"Login URL: {LoginUrl}");
                    State = ConnectionState.Unauthorized;
                }

                // TODO: set timer to refresh tokens and reconnect websocket
            }
        }

        protected async Task SendMessageAsync() {
            await WebSocketDispatcher.SendMessageAsync(new SendMessageRequest {
                ChannelId = "General",
                Text = ChatMessage
            });
            ChatMessage = "";
        }

        protected async Task RenameUserAsync() {
            await WebSocketDispatcher.SendMessageAsync(new RenameUserRequest {
                UserName = UserName
            });
        }

        private void ReceivedMessage(UserMessageNotification message) {
            Console.WriteLine($"Received UserMessage: UserId={message.UserId}, UserName={message.UserName}, ChannelId={message.ChannelId}, Text='{message.Text}', Timestamp={message.Timestamp}");

            // add message to message list
            Messages.Add(message);

            // update user interface
            StateHasChanged();
        }

        private void ReceivedUserNameChanged(UserNameChangedNotification username) {
            Console.WriteLine($"Received UserNameChanged: UserId={username.UserId}, UserName={username.UserName}, OldUserName={username.OldUserName}");

            // check if user name change notification is about us or somebody else
            if(username.UserId == UserId) {

                // update our user name
                UserName = username.UserName;
            }

            // show message about renamed user
            Messages.Add(new UserMessageNotification {
                UserId = "#host",
                Text = $"{username.OldUserName} is now known as {username.UserName}",
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });

            // update user interface
            StateHasChanged();
        }

        private async Task ReceivedWelcomeAsync(WelcomeNotification welcome) {
            Console.WriteLine($"Received Welcome: UserId={welcome.UserId}, UserName={welcome.UserName}");

            // TODO: determine which channel has the focus
            // TODO: add all focused channel messages
            // TODO: highlight all unfocused channels that new messages have arrived

            // update application state
            UserId = welcome.UserId;
            UserName = welcome.UserName;
            State = ConnectionState.Connected;

            var channelIds = welcome.ChannelMessages.Keys.OrderBy(key => key.ToLowerInvariant());
            foreach(var channelId in channelIds) {
                sidebarInfo.Items[0].SubItems.Add(new SidebarItemInfo { To = "#", Text = channelId });
            }

            // add all messages for 'General' channel
            const string channelId = "General";
            IEnumerable<MessageRecord> messages = new List<MessageRecord>();
            if(welcome.ChannelMessages?.TryGetValue(channelId, out messages) ?? false) {
                Messages.AddRange(messages.Select(message => new UserMessageNotification {
                    UserId = message.UserId,
                    UserName = welcome.Users[message.UserId].UserName,
                    ChannelId = channelId,
                    Text = message.Message,
                    Timestamp = message.Timestamp
                }));
            }

            // update user interface
            StateHasChanged();
        }

        private void ReceivedJoinedChannel(JoinedChannelNotification joined) {
            Console.WriteLine($"Received JoinedChannel: UserId={joined.UserId}, UserName={joined.UserName}, ChannelId={joined.ChannelId}");

            // show message about renamed user
            Messages.Add(new UserMessageNotification {
                UserId = "#host",
                Text = $"{joined.UserName} has joined",
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });

            // update user interface
            StateHasChanged();
        }

        private async Task<AuthenticationTokens> GetAuthenticationTokens() {

            // check if any authentication tokens are stored
            var authenticationTokens = await LoadTokensAsync();
            if(authenticationTokens == null) {
                Console.WriteLine($"No authentication tokens found");
                return null;
            }

            // check if tokens will expire in 5 minutes or less
            var authenticationTokenExpiration = DateTimeOffset.FromUnixTimeSeconds(authenticationTokens.Expiration);
            var authenticationTokenTtl = authenticationTokenExpiration - DateTimeOffset.UtcNow;
            if(authenticationTokenTtl < TimeSpan.FromSeconds(TOKEN_EXPIRATION_LIMIT_SECONDS)) {
                Console.WriteLine($"Current authentication tokens has expired or expires soon: {authenticationTokenExpiration}");

                // refresh authentication tokens
                Console.WriteLine($"Refreshing authentication tokens for code grant: {authenticationTokens.IdToken}");
                var oauth2TokenResponse = await HttpClient.PostAsync($"{CognitoSettings.UserPoolUri}/oauth2/token", new FormUrlEncodedContent(new[] {
                    new KeyValuePair<string, string>("grant_type", "refresh_token"),
                    new KeyValuePair<string, string>("client_id", CognitoSettings.ClientId),
                    new KeyValuePair<string, string>("refresh_token", authenticationTokens.RefreshToken)
                }));
                if(!oauth2TokenResponse.IsSuccessStatusCode) {
                    Console.WriteLine("Authentication tokens refresh failed");
                    await ClearAuthenticationTokens();
                    return null;
                }

                // store authentication tokens in local storage
                var json = await oauth2TokenResponse.Content.ReadAsStringAsync();
                Console.WriteLine($"Storing authentication tokens: {json}");
                var refreshAuthenticationTokens = AuthenticationTokens.FromJson(json);
                authenticationTokens.IdToken = refreshAuthenticationTokens.IdToken;
                authenticationTokens.AccessToken = refreshAuthenticationTokens.AccessToken;
                authenticationTokens.Expiration = refreshAuthenticationTokens.Expiration;
                await SaveTokensAsync(authenticationTokens);
            } else {
                Console.WriteLine($"Current authentication tokens valid until: {authenticationTokenExpiration}");
            }
            return authenticationTokens;
        }

        private async Task ClearAuthenticationTokens() {
            Console.WriteLine("Clearing old authentication tokens");
            await ClearTokensAsync();
        }
        
        // private async Task ListUserChannels() {
        //     // TODO: get list of channels for current user from dynamodb

        //     StateHasChanged();
        // }

        //--- IDisposable Members ---
        void IDisposable.Dispose() => WebSocketDispatcher.Dispose();
    }
}