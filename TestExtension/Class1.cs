using Microsoft.Extensions.Hosting;

namespace TestExtension
{
    public class RavenDB { }
    public class MongoDB { }
    public class EventStore { }

    public static class EventStoreWithRavenExtensions
    {
        public static IHostBuilder UseProjectionsWith<TEventStore, TProjectionStore>(this IHostBuilder builder)
            where TEventStore : EventStore
            where TProjectionStore : RavenDB
        {
            return builder;
        }
    }

    public static class EventStoreWithMongoDBExtensions
    {
        public static IHostBuilder UseProjectionsWith<TEventStore, TProjectionStore>(this IHostBuilder builder)
            where TEventStore : EventStore
            where TProjectionStore : MongoDB
        {
            return builder;
        }
    }
}
