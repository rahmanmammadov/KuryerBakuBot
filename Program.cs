using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace KuryerBakuBot
{
    class Program
    {
        static async Task Main(string[] args)
        {
            // 1. Set up the generic .NET Host builder
            var host = Host.CreateDefaultBuilder(args)
                .ConfigureServices((hostContext, services) =>
                {
                    // Registers in-memory caching (essential for our album state tracking and admin cache)
                    services.AddMemoryCache();

                    // Registers our SQLite database service as a Singleton
                    services.AddSingleton<DatabaseService>();

                    // Registers our Telegram Bot Worker as a BackgroundService
                    services.AddHostedService<BotBackgroundService>();
                })
                .Build();

            // 2. Initialize the Database tables before the host starts running
            var databaseService = host.Services.GetRequiredService<DatabaseService>();
            await databaseService.InitializeAsync();

            // 3. Run the application
            await host.RunAsync();
        }
    }
}