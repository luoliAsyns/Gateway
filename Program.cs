using GatewayService.Services.Coupon;
using GatewayService.Services.ExternalOrder;
using LuoliCommon;
using LuoliCommon.Logger;
using LuoliHelper.Utils;
using LuoliUtils;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using RabbitMQ.Client;
using System.Data;
using System.Reflection;
using ThirdApis;
using ILogger = LuoliCommon.Logger.ILogger;

namespace GatewayService
{
    public class Program
    {
        public static Config Config { get; set; }
        private static RabbitMQConnection RabbitMQConnection { get; set; }
        private static RedisConnection RedisConnection { get; set; }

        public static List<string> NotifyUsers;
        private static bool init()
        {
            bool result = false;
            string configFolder = "/app/ExternalOrder/configs";

#if DEBUG
            configFolder = "debugConfigs";
#endif

            ActionsOperator.TryCatchAction(() =>
            {
                Config = new Config($"{configFolder}/sys.json");
                NotifyUsers = Config.KVPairs["NotifyUsers"].Split(',').Select(s => s.Trim()).Where(s => !String.IsNullOrEmpty(s)).ToList();

                RabbitMQConnection = new RabbitMQConnection($"{configFolder}/rabbitmq.json");
                RedisConnection = new RedisConnection($"{configFolder}/redis.json");

                var rds = new CSRedis.CSRedisClient($"{RedisConnection.Host}:{RedisConnection.Port},password={RedisConnection.Password},defaultDatabase={RedisConnection.DatabaseId}");
                RedisHelper.Initialization(rds);

                //SqlClient.DbFirst.IsCreateAttribute().StringNullable().CreateClassFile(@"E:\Code\repos\LuoliHelper\DBModels", "LuoliHelper.DBModels");

                result = true;
            });

            return result;
        }

           


        public static void Main(string[] args)
        {
            #region luoli code

            Environment.CurrentDirectory = AppContext.BaseDirectory;

            if (!init())
            {
                throw new Exception("initial failed; cannot start");
            }

            #endregion


            var builder = WebApplication.CreateBuilder(args);

            // 配置 Kestrel 支持 HTTP/2
            builder.WebHost.ConfigureKestrel(serverOptions =>
            {
                int port = int.Parse(Config.KVPairs["BindPort"]);

                serverOptions.ListenAnyIP(port, options => {
                    options.Protocols = HttpProtocols.Http1AndHttp2;
                });
            });


            // Add services to the container.

            builder.Services.AddControllers();
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddScoped<AsynsApis>(provider =>
            {
                ILogger logger = provider.GetRequiredService<ILogger>();
                return new AsynsApis(logger, Config.KVPairs["AsynsApiUrl"]);
            });
            builder.Services.AddScoped<AgisoApis>();

            builder.Services.AddScoped<IExternalOrderRepository, ExternalOrderRepository>();
            builder.Services.AddScoped<ICouponRepository, CouponRepository>();


            #region ���ILogger

            builder.Services.AddHttpClient("LokiHttpClient")
                .ConfigureHttpClient(client =>
                {
                    // ��������ͳһ���� HttpClient�������SSL ���ԣ������������ã�
                    // client.DefaultRequestHeaders.Add("X-Custom-Header", "luoli-app");
                });

            //�����Ƕ�ԭʼhttp client logger����filter
            builder.Logging.AddFilter(
                "System.Net.Http.HttpClient.LokiHttpClient",
                LogLevel.Warning
            );

            // ע��LokiLogger�������񣨷�װ��־������
            builder.Services.AddSingleton<ILogger, LokiLogger>(provider =>
            {
                var httpClient = provider.GetRequiredService<IHttpClientFactory>()
                    .CreateClient("LokiHttpClient");

                var loki = new LokiLogger(Config.KVPairs["LokiEndPoint"],
                    new Dictionary<string, string>(),
                    httpClient);
                loki.AfterLog = (msg) => Console.WriteLine(msg);

                ActionsOperator.Initialize(loki);
                return loki;
            });



            #endregion

            #region ���rabbitmq

            // ע��RabbitMQ���ӹ���
            builder.Services.AddScoped<RabbitMQ.Client.IConnectionFactory>(provider =>
            {
                return new ConnectionFactory
                {
                    HostName = RabbitMQConnection.Host,
                    Port = RabbitMQConnection.Port,
                    UserName = RabbitMQConnection.UserId,
                    Password = RabbitMQConnection.UserId,
                    VirtualHost = "/",
                    // ����ʧ��ʱ�Զ�����
                    AutomaticRecoveryEnabled = true,
                    NetworkRecoveryInterval = TimeSpan.FromSeconds(5)
                };
            });

            // ע��RabbitMQ����
            builder.Services.AddScoped<IConnection>(provider =>
            {
                var factory = provider.GetRequiredService<RabbitMQ.Client.IConnectionFactory>();
                return factory.CreateConnectionAsync().Result;
            });

            // ע��RabbitMQͨ��
            builder.Services.AddScoped<IChannel>(provider =>
            {
                var connection = provider.GetRequiredService<IConnection>();
                return connection.CreateChannelAsync().Result;
            });

            #endregion


            var app = builder.Build();

            ServiceLocator.Initialize(app.Services);

            #region luoli code

            // Ӧ��������ͨ������������ȡ LokiLogger ʵ��
            using (var scope = app.Services.CreateScope())
            {
                var services = scope.ServiceProvider;
                try
                {
                    // ��ȡ LokiLogger ʵ��
                    var lokiLogger = services.GetRequiredService<LuoliCommon.Logger.ILogger>();

                    lokiLogger.Info("app starting");
                    lokiLogger.Debug($"env:{app.Environment.EnvironmentName}, listening: {Config.BindAddr}");


                    var assembly = Assembly.GetExecutingAssembly();
                    var fileVersionInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(assembly.Location);
                    var fileVersion = fileVersionInfo.FileVersion;

                    lokiLogger.Info($"CurrentDirectory:[{Environment.CurrentDirectory}]");
                    lokiLogger.Info($"Current File Version:[{fileVersion}]");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"while using loki:{ex.Message}");
                }
            }


            #endregion

            app.MapControllers();

            app.Run(Config.BindAddr);
        }
    }
}
