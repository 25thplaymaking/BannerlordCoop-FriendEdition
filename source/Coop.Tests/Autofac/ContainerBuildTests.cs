using Autofac;
using Common.LogicStates;
using Common.Network;
using Common.Network.Session;
using Coop.Core.Common.Configuration;
using Coop.Core.Client;
using Coop.Core.Server;
using GameInterface;
using GameInterface.Services.WorkshopMods.Core;
using Coop.Core.Client.States;
using Coop.Core.Server.Connections;
using System.Linq;
using Xunit;

namespace Coop.Tests.Autofac
{
    public class ContainerBuildTests
    {
        [Fact]
        public void Client_Container_Build()
        {
            ContainerBuilder builder = new ContainerBuilder();
            builder.RegisterModule<ClientModule>();
            builder.RegisterModule<GameInterfaceModule>();
            using var container = builder.Build();

            Assert.NotNull(container);

            var client = container.Resolve<INetwork>();
            Assert.NotNull(client);

            var logic = container.Resolve<ILogic>();
            Assert.NotNull(logic);
            Assert.IsType<WorkshopManifestProvider>(container.Resolve<IWorkshopManifestProvider>());
        }

        [Fact]
        public void Server_Container_Build()
        {
            ContainerBuilder builder = new ContainerBuilder();
            builder.RegisterModule<ServerModule>();
            builder.RegisterModule<GameInterfaceModule>();
            using var container = builder.Build();

            Assert.NotNull(container);

            var server = container.Resolve<INetwork>();
            Assert.NotNull(server);

            var logic = container.Resolve<ILogic>();
            Assert.NotNull(logic);
            Assert.IsType<WorkshopManifestProvider>(container.Resolve<IWorkshopManifestProvider>());
        }

        [Fact]
        public void ProductionContexts_DoNotExposeOptionalWorkshopHandshakeFallback()
        {
            Assert.False(typeof(ClientContext).GetConstructors().Single()
                .GetParameters().Single(parameter =>
                    parameter.ParameterType == typeof(IWorkshopManifestProvider)).IsOptional);
            Assert.False(typeof(ConnectionContext).GetConstructors().Single()
                .GetParameters().Single(parameter =>
                    parameter.ParameterType == typeof(IWorkshopManifestProvider)).IsOptional);
        }

        [Theory]
        [InlineData(ServerVisibility.FriendsOnly)]
        [InlineData(ServerVisibility.None)]
        public void Server_Container_UsesHostSelectedAdvertisementConfig(ServerVisibility visibility)
        {
            var selectedConfig = new SessionAdvertisementConfig { Visibility = visibility };
            ContainerBuilder builder = new ContainerBuilder();
            builder.RegisterModule<ServerModule>();
            builder.RegisterModule<GameInterfaceModule>();
            builder.RegisterInstance(selectedConfig).AsSelf().SingleInstance();

            using var container = builder.Build();

            Assert.Same(selectedConfig, container.Resolve<SessionAdvertisementConfig>());
            Assert.Equal(visibility, container.Resolve<SessionAdvertisementConfig>().Visibility);
        }
    }
}
