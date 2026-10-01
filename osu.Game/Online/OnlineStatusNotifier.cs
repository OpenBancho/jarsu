// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions.ObjectExtensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Screens;
using osu.Game.Localisation;
using osu.Game.Online.API;
using osu.Game.Online.Metadata;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Notifications.WebSocket;
using osu.Game.Online.Spectator;
using osu.Game.Overlays;
using osu.Game.Overlays.Notifications;
using osu.Game.Screens.OnlinePlay;
using osu.Game.Screens.Play;

namespace osu.Game.Online
{
    /// <summary>
    /// Handles various scenarios where connection is lost and we need to let the user know what and why.
    /// </summary>
    public partial class OnlineStatusNotifier : Component
    {
        private readonly Func<IScreen> getCurrentScreen;

        private INotificationsClient notificationsClient = null!;

        [Resolved]
        private MultiplayerClient multiplayerClient { get; set; } = null!;

        [Resolved]
        private SpectatorClient spectatorClient { get; set; } = null!;

        [Resolved]
        private MetadataClient metadataClient { get; set; } = null!;

        [Resolved]
        private INotificationOverlay? notificationOverlay { get; set; }

        private IBindable<APIState> apiState = null!;
        private IBindable<bool> multiplayerState = null!;
        private IBindable<bool> spectatorState = null!;

        /// <summary>
        /// This flag will be set to <c>true</c> when the user has been notified so we don't show more than one notification.
        /// </summary>
        private bool userNotified;

        private bool welcomed;
        private IAPIProvider api = null!;

        public OnlineStatusNotifier(Func<IScreen> getCurrentScreen)
        {
            this.getCurrentScreen = getCurrentScreen;
        }

        [BackgroundDependencyLoader]
        private void load(IAPIProvider api)
        {
            this.api = api;
            apiState = api.State.GetBoundCopy();
            notificationsClient = api.NotificationsClient;
            multiplayerState = multiplayerClient.IsConnected.GetBoundCopy();
            spectatorState = spectatorClient.IsConnected.GetBoundCopy();

            notificationsClient.MessageReceived += notifyAboutForcedDisconnection;
            multiplayerClient.Disconnecting += notifyAboutForcedDisconnection;
            spectatorClient.Disconnecting += notifyAboutForcedDisconnection;
            metadataClient.Disconnecting += notifyAboutForcedDisconnection;
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            apiState.BindValueChanged(state =>
            {
                switch (state.NewValue)
                {
                    case APIState.Online:
                        userNotified = false;
                        checkWelcomeNotification();
                        return;

                    case APIState.Offline:
                        welcomed = false;
                        if (getCurrentScreen() is OnlinePlayScreen)
                            notifyApiDisconnection();
                        break;
                }
            });

            checkWelcomeNotification();

            multiplayerState.BindValueChanged(connected => Schedule(() =>
            {
                if (connected.NewValue)
                {
                    userNotified = false;
                    return;
                }

                if (multiplayerClient.Room != null)
                    notifyApiDisconnection();
            }));

            spectatorState.BindValueChanged(connected => Schedule(() =>
            {
                if (connected.NewValue)
                {
                    userNotified = false;
                    return;
                }

                switch (getCurrentScreen())
                {
                    case SpectatorPlayer: // obvious issues
                    case SubmittingPlayer: // replay sending issues
                        notifyApiDisconnection();
                        break;
                }
            }));
        }

        private void checkWelcomeNotification()
        {
            if (welcomed) return;
            if (apiState.Value != APIState.Online) return;
            if (string.IsNullOrWhiteSpace(api.ServerName)) return;

            welcomed = true;
            string versionStr = !string.IsNullOrWhiteSpace(api.ServerVersion) ? api.ServerVersion : "1.0.0";
            notificationOverlay?.Post(new SimpleNotification
            {
                Icon = FontAwesome.Solid.CheckCircle,
                Text = $"Welcome to {api.ServerName} ver {versionStr}",
            });
        }

        private void notifyApiDisconnection()
        {
            if (userNotified) return;

            userNotified = true;
            notificationOverlay?.Post(new SimpleErrorNotification
            {
                Icon = FontAwesome.Solid.ExclamationCircle,
                Text = NotificationsStrings.APIConnectionInterrupted,
            });
        }

        private void notifyAboutForcedDisconnection()
        {
            if (userNotified) return;

            userNotified = true;
            notificationOverlay?.Post(new SimpleErrorNotification
            {
                Icon = FontAwesome.Solid.ExclamationCircle,
                Text = NotificationsStrings.AnotherDeviceDisconnect,
            });
        }

        private void notifyAboutForcedDisconnection(SocketMessage obj)
        {
            if (obj.Event != @"logout") return;

            if (userNotified) return;

            userNotified = true;
            notificationOverlay?.Post(new SimpleErrorNotification
            {
                Icon = FontAwesome.Solid.ExclamationCircle,
                Text = NotificationsStrings.AccountChangeDisconnect,
            });
        }

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);

            if (notificationsClient.IsNotNull())
                notificationsClient.MessageReceived -= notifyAboutForcedDisconnection;

            if (spectatorClient.IsNotNull())
                spectatorClient.Disconnecting -= notifyAboutForcedDisconnection;

            if (multiplayerClient.IsNotNull())
                multiplayerClient.Disconnecting -= notifyAboutForcedDisconnection;

            if (metadataClient.IsNotNull())
                metadataClient.Disconnecting -= notifyAboutForcedDisconnection;
        }
    }
}
