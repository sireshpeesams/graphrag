using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.AppConfig;
using SearchFrontend.Common;
using SearchFrontend.DataSources.AzureDevOps;
using SearchFrontend.DataSources.Bing;
using SearchFrontend.DataSources.GPT;
using SearchFrontend.DataSources.Interface;
using SearchFrontend.DataSources.Nirvana;
using SearchFrontend.DataSources.QnAMaker;
using SearchFrontend.Endpoints.Completions;
using SearchFrontend.Endpoints.Score;
using SearchFrontend.Providers.AzureDevOps;
using SearchFrontend.Providers.Bing;
using SearchFrontend.Providers.GPT;
using SearchFrontend.Providers.Interface;
using SearchFrontend.Providers.Nirvana;
using ProviderLibrary.AuthenticationProvider.Abstractions;
using ProviderLibrary.AuthenticationProvider.Implementations;
using ProvidersLibrary.HTTPProviders.Implementations;
using ProviderLibrary.HTTPProviders.Abstractions;
using ProviderLibrary.DataProviders.Implementations;
using ProviderLibrary.DataProviders.Abstractions;
using SearchFrontend.Endpoints.CriEscalationAdvisor;
using ProvidersLibrary.DataProviders.Implementations;
using BotLibrary.Interactions;
using Microsoft.Bot.Builder.Integration.AspNet.Core;
using SearchFrontend.Providers.Ai;
using ProvidersLibrary.DataProviders.Abstractions;
using SearchFrontend.Providers.FileStore;
using SearchFrontend.Endpoints.CriHealthSnapshot.Services;
using SearchFrontend.Endpoints.GraphRAG;
using Clc.Common.Repositories;
using Clc.Common.Entities;

//using Azure.Core;
//using Azure.Identity;
using SearchFrontend.Endpoints.InputBlock.CaseAnalysisTool;
using Azure.Core;
using Azure.Identity;
using Clc.Common.Helpers;

var environment = ConfigHelper.GetVariable<string>("AZURE_FUNCTIONS_ENVIRONMENT");

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .ConfigureServices(services =>
    {

        // Set debug mode based on environment variable
        bool isDebugMode = ConfigHelper.GetVariable<bool>("DEBUG_MODE", false);
        string VaultUrl = ConfigHelper.GetVariable<string>("PMEKeyVaultURI");
        KeyVaultHelperService keyVaultService;
        if (isDebugMode)
        {
            keyVaultService = new KeyVaultHelperService(VaultUrl, ConfigHelper.GetVariable<string>("BearerToken"), true);
        }
        else
        {
            keyVaultService = new KeyVaultHelperService(VaultUrl, ConfigHelper.GetVariable<string>("ManagedIdentityId"));
        }


        // Initialize managed identity application for managed identity authentication
        string identityId = ConfigHelper.GetVariable<string>("ManagedIdentityId");
        var managedIdentityApplication = ManagedIdentityApplicationBuilder
            .Create(ManagedIdentityId.WithUserAssignedClientId(identityId))
            .Build();

        var providers = new Dictionary<string, IAuthenticationProvider>();
        var sp_corp_authProvider = new ConfidentialClientAuthenticationProvider(
                ConfidentialClientApplicationBuilder.Create(ConfigHelper.GetVariable<string>("SP_SUPPORTABILITY_CLIENT_ID"))
                    .WithCertificate(keyVaultService.GetCertificateAsync(ConfigHelper.GetVariable<string>("KeyVaultCertificateName")).Result, true)
                    .WithTenantId(ConfigHelper.GetVariable<string>("TenantID"))
                    .Build());
        providers[CommonConstants.AuthenticationProviders.SQL_UDP_REPOSITORY_PROVIDER] = sp_corp_authProvider;
        providers[CommonConstants.AuthenticationProviders.ZEBRA_AI_PROVIDER] = sp_corp_authProvider;


        if (environment == "Development")
        {
            // Initialize required AuthProviders for each service type
            var sp_pme_authProvider = new ConfidentialClientAuthenticationProvider(
                ConfidentialClientApplicationBuilder.Create(ConfigHelper.GetVariable<string>("SP_SUPPORTABILITY_CLIENT_ID"))
                    .WithCertificate(keyVaultService.GetCertificateAsync(ConfigHelper.GetVariable<string>("KeyVaultCertificateName")).Result, true)
                    .WithTenantId(ConfigHelper.GetVariable<string>("PMETenantID"))
                    .Build());


            providers[CommonConstants.AuthenticationProviders.AZURE_COMMON_REPOSITORY_PROVIDER] = sp_pme_authProvider;
            providers[CommonConstants.AuthenticationProviders.AZURE_STORAGE_PROVIDER] = sp_pme_authProvider;


            services.AddScoped(typeof(ICaseRepository), ConfigHelper.GetVariable<Type>("TYPE_CASE_REPOSITORY", typeof(MockCaseRepository)));
            services.AddScoped(typeof(BaseAiProvider), ConfigHelper.GetVariable<Type>("TYPE_AI_PROVIDER", typeof(MockAiProvider)));
            services.AddScoped(typeof(BaseFileStore), ConfigHelper.GetVariable<Type>("TYPE_FILE_STORE_PROVIDER", typeof(LocalFileStore)));



            services.AddSingleton<BaseBotInteraction>(new MockBotInteraction(
                LoggerFactory.Create(loggingBuilder => loggingBuilder
                      .SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Debug)
                      .AddConsole()).CreateLogger<IBotFrameworkHttpAdapter>(),
                       "c:\\temp\\",
                       ConfigHelper.GetVariable<string>("KustoClusterDnaisupportabilityIngest"),
                       ConfigHelper.GetVariable<string>("CRIESCALATIONADVISOR_KUSTO_DB"),
                       ConfigHelper.GetVariable<string>("SP_SUPPORTABILITY_CLIENT_ID"),
                       keyVaultService,
                       ConfigHelper.GetVariable<string>("LOGIN_ENDPOINT_URL"),
                       ConfigHelper.GetVariable<string>("TenantID"),
                       ConfigHelper.GetVariable<string>("KeyVaultCertificateName"),
                       ConfigHelper.GetVariable<string>("PMETenantID")
                    )
                );

            //var clientApplication = ConfidentialClientApplicationBuilder
            //    .Create(ConfigHelper.GetVariable<string>("SP_SUPPORTABILITY_CLIENT_ID"))
            //    .WithCertificate(keyVaultService.GetCertificateAsync(ConfigHelper.GetVariable<string>("KeyVaultCertificateName")).Result, true)
            //    .WithTenantId(ConfigHelper.GetVariable<string>("PMETenantID"))
            //    .Build();
            services.AddSingleton<ISearchProvider, SearchProvider>(x =>
               new SearchProvider(new ConfidentialClientAuthenticationProvider(sp_pme_authProvider.GetApplication() as IConfidentialClientApplication)));
        }
        else
        {
            // Initialize required AuthProviders for each service type using managed identity

            var mi_authProvider = new ManagedIdentityAuthenticationProvider(managedIdentityApplication);
            providers[CommonConstants.AuthenticationProviders.AZURE_COMMON_REPOSITORY_PROVIDER] = mi_authProvider;
            providers[CommonConstants.AuthenticationProviders.AZURE_STORAGE_PROVIDER] = mi_authProvider;


            services.AddScoped(typeof(ICaseRepository), ConfigHelper.GetVariable<Type>("TYPE_CASE_REPOSITORY", typeof(SqlCaseRepository)));
            services.AddScoped(typeof(BaseAiProvider), ConfigHelper.GetVariable<Type>("TYPE_AI_PROVIDER", typeof(ZebraAiProvider)));
            services.AddScoped(typeof(BaseFileStore), ConfigHelper.GetVariable<Type>("TYPE_FILE_STORE_PROVIDER", typeof(AzureStorageFileStore)));

            services.AddSingleton<BaseBotInteraction>(
              new BotInteraction(
                  ConfigHelper.GetVariable<string>("STORAGECONTAINER_URL"),
                  ConfigHelper.GetVariable<string>("ManagedIdentityId"),
                  ConfigHelper.GetVariable<string>("TEAMS_CHANNEL_ID"),
                  new SQLQueryExecutor(
                      ConfigHelper.GetVariable<string>("SP_SUPPORTABILITY_CLIENT_ID"),
                      keyVaultService,
                      ConfigHelper.GetVariable<string>("LOGIN_ENDPOINT_URL"),
                      ConfigHelper.GetVariable<string>("PMETenantID"),
                      ConfigHelper.GetVariable<string>("KeyVaultCertificateName"),
                      ConfigHelper.GetVariable<string>("PMETenantID")),
                  ConfigHelper.GetVariable<string>("Server"),
                  ConfigHelper.GetVariable<string>("DatabaseName"),
                  new GptChatProviderV2(
                      ConfigHelper.GetVariable<string>("CHAT_GPT_35_TURBO_URL"),
                      new ManagedIdentityAuthenticationProvider(managedIdentityApplication),
                      //new ConfidentialClientAuthenticationProvider(clientApplication),
                      ConfigHelper.GetVariable<string>("GptScope")),
                  new ManagedIdentityAuthenticationProvider(managedIdentityApplication),
                  ConfigHelper.GetVariable<string>("Scope"),
                  ConfigHelper.GetVariable<string>("completionsEndPoint"),
                  ConfigHelper.GetVariable<string>("KustoserverAIingest"),
                  ConfigHelper.GetVariable<string>("kustoDatabaseDnaicommondb"),
                  ConfigHelper.GetVariable<string>("CRIESCALATIONADVISOR_KUSTO_DB"),
                  ConfigHelper.GetVariable<string>("kustoTable"),
                  LoggerFactory.Create(loggingBuilder => loggingBuilder
                      .SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Trace)
                      .AddConsole()).CreateLogger<IBotFrameworkHttpAdapter>()
              ));

            services.AddSingleton<ISearchProvider, SearchProvider>(x =>
               new SearchProvider(new ManagedIdentityAuthenticationProvider(managedIdentityApplication)));
        }

        services.AddSingleton<IDictionary<string, IAuthenticationProvider>>(providers);
        services.AddSingleton<IAuthenticationProviderFactory, AuthenticationProviderFactory>();

        services.AddScoped(typeof(ISummaryService), ConfigHelper.GetVariable<Type>("TYPE_SUMMARY_SERVICE", typeof(AiSummaryService)));

        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();
        services.AddHttpClient("nirvana", c =>
        {
            c.BaseAddress = new Uri(ConfigHelper.GetVariable<string>("NirvanaUri"));
            c.DefaultRequestHeaders.Add("Authorization", "Bearer " + ConfigHelper.GetVariable<string>("NirvanaKey"));
            c.DefaultRequestHeaders.Add("azureml-model-deployment", "green");
        });
        services.AddLogging();
        services.AddHttpClient("qnaMaker", c =>
        {
            c.BaseAddress = new Uri(ConfigHelper.GetVariable<string>("QnaUri"));
        });

        services.AddHttpClient("qnaMakerV2", c =>
        {
            c.BaseAddress = new Uri(ConfigHelper.GetVariable<string>("QnaUriV2"));
        });

        services.AddHttpClient("bing", c =>
        {
            c.BaseAddress = new Uri(ConfigHelper.GetVariable<string>("BING_API_URL"));
            c.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", ConfigHelper.GetVariable<string>("BING_OCP_APIM_SUBSCRIPTION_KEY"));
        });

        services.AddHttpClient("gpt");

        services.AddSingleton<IKeyVaultHelperService, KeyVaultHelperService>(service => keyVaultService);

        if (isDebugMode)
        {
            // Initialize client application for confidential client authentication
            string clientId = ConfigHelper.GetVariable<string>("SP_SUPPORTABILITY_CLIENT_ID");
            string tenantIdPME = ConfigHelper.GetVariable<string>("PMETenantID");
            var clientApplicationPME = ConfidentialClientApplicationBuilder
               .Create(clientId)
               .WithCertificate(keyVaultService.GetCertificateAsync(ConfigHelper.GetVariable<string>("KeyVaultCertificateName")).Result, true)
               .WithTenantId(tenantIdPME)
               .Build();
            services.AddSingleton<IAuthenticationProvider>(new ConfidentialClientAuthenticationProvider(clientApplicationPME));
            services.AddSingleton<IGptChatProviderV2, GptChatProviderV2>(x =>
               new GptChatProviderV2(ConfigHelper.GetVariable<string>("CHAT_GPT_4_MINI_URL"), new ConfidentialClientAuthenticationProvider(clientApplicationPME), ConfigHelper.GetVariable<string>("GptScope")));
            services.AddSingleton<IGptCompletionProviderv2, GptCompletionProviderv2>(x =>
           new GptCompletionProviderv2(ConfigHelper.GetVariable<string>("CHAT_GPT_35_TURBO_URL"), new ConfidentialClientAuthenticationProvider(clientApplicationPME), ConfigHelper.GetVariable<string>("GptScope")));
            services.AddSingleton<IQnaProviderV2, QnaProviderV2>(x =>
               new QnaProviderV2(new ConfidentialClientAuthenticationProvider(clientApplicationPME)));

            services.AddSingleton<ICaseDataService, CaseDataService>(x =>
                new CaseDataService(ConfigHelper.GetVariable<string>("SP_SUPPORTABILITY_CLIENT_ID"),
                                    keyVaultService,
                                    ConfigHelper.GetVariable<string>("LOGIN_ENDPOINT_URL"),
                                    ConfigHelper.GetVariable<string>("TenantID"),
                                    ConfigHelper.GetVariable<string>("KeyVaultCertificateName"),
                                    new ConfidentialClientAuthenticationProvider(clientApplicationPME),

                                    ConfigHelper.GetVariable<string>("CHAT_GPT_4_MINI_URL"),
                                    ConfigHelper.GetVariable<string>("GptScope"),

                                    ConfigHelper.GetVariable<string>("KustoserverAIingest"),
                                    ConfigHelper.GetVariable<string>("KustoServerAI"),
                                    ConfigHelper.GetVariable<string>("KustoDatabaseAI"),

                                    ConfigHelper.GetVariable<string>("KustoTableAI"),
                                    ConfigHelper.GetVariable<string>("KustoServerCase"),
                                    ConfigHelper.GetVariable<string>("KustoDatabaseCase"),

                                    null,
                                    tenantIdPME));

            services.AddSingleton<IAutoContentService, AutoContentService>(x =>
             new AutoContentService(ConfigHelper.GetVariable<string>("SP_SUPPORTABILITY_CLIENT_ID"),
                                 keyVaultService,
                                 ConfigHelper.GetVariable<string>("LOGIN_ENDPOINT_URL"),
                                 ConfigHelper.GetVariable<string>("TenantID"),
                                 ConfigHelper.GetVariable<string>("KeyVaultCertificateName"),
                                "https://dnaisupportability.eastus.kusto.windows.net",
                                  "dnaicommondb_ppe",
                                 null,
                                 tenantIdPME));

            services.AddSingleton<IIcMDataService, IcMDataService>(x =>
               new IcMDataService(ConfigHelper.GetVariable<string>("SP_SUPPORTABILITY_CLIENT_ID"),
                                   keyVaultService,
                                   ConfigHelper.GetVariable<string>("LOGIN_ENDPOINT_URL"),
                                   ConfigHelper.GetVariable<string>("TenantID"),
                                   ConfigHelper.GetVariable<string>("KeyVaultCertificateName"),
                                   new ConfidentialClientAuthenticationProvider(clientApplicationPME),

                                   ConfigHelper.GetVariable<string>("CHAT_GPT_4_MINI_URL"),
                                   ConfigHelper.GetVariable<string>("GptScope"),

                                    ConfigHelper.GetVariable<string>("IcMKustoserverAIingest"), //: "https://ingest-dnaisupportability.eastus.kusto.windows.net",
                                    ConfigHelper.GetVariable<string>("IcMKustoserverAI"),// : "https://dnaisupportability.eastus.kusto.windows.net",
                                    ConfigHelper.GetVariable<string>("IcMKustoDatabaseAI_ppe"), // :"dnaicommondb_ppe",
                                    ConfigHelper.GetVariable<string>("IcMKustoTableAI"),// : "IcMSummary",
                                     ConfigHelper.GetVariable<string>("IcMDataWarehouseKustoServer"),//:  "https://dnaiicmfollower.centralus.kusto.windows.net",
                                     ConfigHelper.GetVariable<string>("IcMDataWarehouse"),// : "IcMDataWarehouse",

                                   null,
                                   tenantIdPME));

            string clientId2 = ConfigHelper.GetVariable<string>("ClientID");
            string tenantId = ConfigHelper.GetVariable<string>("TenantID");
            var options = new ClientCertificateCredentialOptions()
            {
                SendCertificateChain = true,
            };
            services.AddSingleton<TokenCredential>(identity => new ClientCertificateCredential(
                                                              ConfigHelper.GetVariable<string>("PMETenantID"),
                                                              clientId2,
                                                              keyVaultService.GetCertificateAsync(ConfigHelper.GetVariable<string>("KeyVaultCertificateName")).Result,
                                                              options));





            if (environment == "Development")
            {
                //services.AddSingleton<IDataSource>(new MockDataSource(ConfigHelper.GetVariable<string>("MOCK_DATA_SOURCE_PATH", "c:/temp")));
                services.AddSingleton<IDataSource>(provider => new KustoQueryExecutor(
                                        ConfigHelper.GetVariable<string>("SP_SUPPORTABILITY_CLIENT_ID"),
                                        keyVaultService,
                                        ConfigHelper.GetVariable<string>("LOGIN_ENDPOINT_URL"),
                                        ConfigHelper.GetVariable<string>("TenantID"),
                                        ConfigHelper.GetVariable<string>("KeyVaultCertificateName"),
                                        ConfigHelper.GetVariable<string>("PMETenantID")));
            }
            else
            {
                services.AddSingleton<IDataSource>(provider => new KustoQueryExecutor(
                                                                    ConfigHelper.GetVariable<string>("SP_SUPPORTABILITY_CLIENT_ID"),
                                                                    keyVaultService,
                                                                    ConfigHelper.GetVariable<string>("LOGIN_ENDPOINT_URL"),
                                                                    ConfigHelper.GetVariable<string>("TenantID"),
                                                                    ConfigHelper.GetVariable<string>("KeyVaultCertificateName"),
                                                                    ConfigHelper.GetVariable<string>("PMETenantID")));
            }
            services.AddSingleton<IPowerBIDatasetExecutor, PowerBIDatasetExecutor>(x => new PowerBIDatasetExecutor(keyVaultService));
            services.AddSingleton<CaseAnalyzer>();

        }
        else
        {
            services.AddSingleton<IAuthenticationProvider>(new ManagedIdentityAuthenticationProvider(managedIdentityApplication));
            services.AddSingleton<IGptChatProviderV2, GptChatProviderV2>(x =>
               new GptChatProviderV2(ConfigHelper.GetVariable<string>("CHAT_GPT_4_MINI_URL"), new ManagedIdentityAuthenticationProvider(managedIdentityApplication), ConfigHelper.GetVariable<string>("GptScope")));
            services.AddSingleton<IGptCompletionProviderv2, GptCompletionProviderv2>(x =>
            new GptCompletionProviderv2(ConfigHelper.GetVariable<string>("CHAT_GPT_35_TURBO_URL"), new ManagedIdentityAuthenticationProvider(managedIdentityApplication), ConfigHelper.GetVariable<string>("GptScope")));
            services.AddSingleton<IQnaProviderV2, QnaProviderV2>(x =>
               new QnaProviderV2(new ManagedIdentityAuthenticationProvider(managedIdentityApplication)));

            services.AddSingleton<ICaseDataService, CaseDataService>(x =>
                new CaseDataService(ConfigHelper.GetVariable<string>("SP_SUPPORTABILITY_CLIENT_ID"),
                                    keyVaultService,
                                    ConfigHelper.GetVariable<string>("LOGIN_ENDPOINT_URL"),
                                    ConfigHelper.GetVariable<string>("TenantID"),
                                    ConfigHelper.GetVariable<string>("KeyVaultCertificateName"),
                                    new ManagedIdentityAuthenticationProvider(managedIdentityApplication),

                                     ConfigHelper.GetVariable<string>("CHAT_GPT_4_MINI_URL"),
                                    ConfigHelper.GetVariable<string>("GptScope"),
                                    ConfigHelper.GetVariable<string>("KustoserverAIingest"),
                                    ConfigHelper.GetVariable<string>("KustoServerAI"),
                                    ConfigHelper.GetVariable<string>("KustoDatabaseAI"),
                                    ConfigHelper.GetVariable<string>("KustoTableAI"),
                                    ConfigHelper.GetVariable<string>("KustoServerCase"),
                                    ConfigHelper.GetVariable<string>("KustoDatabaseCase"),
                                    ConfigHelper.GetVariable<string>("ManagedIdentityId"),
                                    ConfigHelper.GetVariable<string>("PMETenantID")));

            services.AddSingleton<IAutoContentService, AutoContentService>(x =>
             new AutoContentService(ConfigHelper.GetVariable<string>("SP_SUPPORTABILITY_CLIENT_ID"),
                                 keyVaultService,
                                 ConfigHelper.GetVariable<string>("LOGIN_ENDPOINT_URL"),
                                 ConfigHelper.GetVariable<string>("TenantID"),
                                 ConfigHelper.GetVariable<string>("KeyVaultCertificateName"),
                                 "https://dnaisupportability.eastus.kusto.windows.net",
                                 "dnaicommondb_ppe",
                                 ConfigHelper.GetVariable<string>("ManagedIdentityId"),
                                 ConfigHelper.GetVariable<string>("PMETenantID")));

            services.AddSingleton<IIcMDataService, IcMDataService>(x =>
          new IcMDataService(ConfigHelper.GetVariable<string>("SP_SUPPORTABILITY_CLIENT_ID"),
                              keyVaultService,
                              ConfigHelper.GetVariable<string>("LOGIN_ENDPOINT_URL"),
                              ConfigHelper.GetVariable<string>("TenantID"),
                              ConfigHelper.GetVariable<string>("KeyVaultCertificateName"),
                              new ManagedIdentityAuthenticationProvider(managedIdentityApplication),

                              ConfigHelper.GetVariable<string>("CHAT_GPT_4_MINI_URL"),
                              ConfigHelper.GetVariable<string>("GptScope"),

                                     ConfigHelper.GetVariable<string>("IcMKustoserverAIingest"),
                                     ConfigHelper.GetVariable<string>("IcMKustoserverAI"),
                                     ConfigHelper.GetVariable<string>("IcMKustoDatabaseAI_ppe"),
                                     ConfigHelper.GetVariable<string>("IcMKustoTableAI"),
                                     ConfigHelper.GetVariable<string>("IcMDataWarehouseKustoServer"),
                                     ConfigHelper.GetVariable<string>("IcMDataWarehouse"),

                                     ConfigHelper.GetVariable<string>("ManagedIdentityId"),
                                     ConfigHelper.GetVariable<string>("PMETenantID")));



            if (environment == "Development")
            {
                //services.AddSingleton<IDataSource>(new MockDataSource(ConfigHelper.GetVariable<string>("MOCK_DATA_SOURCE_PATH", "c:/temp")));
                services.AddSingleton<IDataSource>(provider => new KustoQueryExecutor(
                                        ConfigHelper.GetVariable<string>("SP_SUPPORTABILITY_CLIENT_ID"),
                                        keyVaultService,
                                        ConfigHelper.GetVariable<string>("LOGIN_ENDPOINT_URL"),
                                        ConfigHelper.GetVariable<string>("TenantID"),
                                        ConfigHelper.GetVariable<string>("KeyVaultCertificateName"),
                                        ConfigHelper.GetVariable<string>("PMETenantID")));
            }
            else
            {
                services.AddSingleton<IDataSource>(provider => new KustoQueryExecutor(ConfigHelper.GetVariable<string>("ManagedIdentityId")));
            }

            services.AddSingleton<IPowerBIDatasetExecutor, PowerBIDatasetExecutor>(x => new PowerBIDatasetExecutor(keyVaultService));

            services.AddSingleton<TokenCredential>(identity => new ManagedIdentityCredential(
                                                identityId));


        }



        services.AddTransient<IDataSource<ScoreRequestBody, ObjectResult>, QnaDataSource>();
        services.AddTransient<IDataSource<ScoreRequestBody, ObjectResult>, NirvanaDataSource>();
        services.AddTransient<IDataSource<ScoreRequestBody, ObjectResult>, BingDataSource>();
        services.AddTransient<IProvider<QnaRequestBody>, SearchFrontend.Providers.QnAMaker.QnaProviderV2>();
        services.AddTransient<IProvider<ScoreRequestBody>, NirvanaProvider>();
        services.AddTransient<IProvider<ScoreRequestBody>, BingProvider>();
        services.AddTransient<IProvider<QnaRequestBody>, BingProviderV2>();
        services.AddTransient<IProvider<GptRequestBody>, GptProvider>();
        services.AddTransient<IProvider<GptChatRequestBody>, SearchFrontend.Providers.GPT.GptChatProvider>();
        services.AddTransient<SearchFrontend.Providers.QnAMaker.QnaProviderGetServiceList>();

        services.AddTransient<IDataSource<GptRequestBody, string>, GptDataSource>();
        services.AddTransient<IDataSource<GptChatRequestBody, string>, GptChatDataSource>();
        services.AddTransient<IDataSource<QnaRequestBody, QnAAnswer>, SearchQnaDataSource>();
        services.AddTransient<IDataSource<QnaRequestBody, QnAAnswer>, SearchBingDataSource>();

        services.AddTransient<IAzureDevOpsProvider, AzureDevOpsProvider>();
        services.AddTransient<IAzureDevOpsService, AzureDevOpsService>();

        //add IOC required for case review processor
        services.AddSingleton<SQLQueryExecutor>(new SQLQueryExecutor(ConfigHelper.GetVariable<string>("SP_SUPPORTABILITY_CLIENT_ID"),
                      keyVaultService,
                      ConfigHelper.GetVariable<string>("LOGIN_ENDPOINT_URL"),
                      ConfigHelper.GetVariable<string>("TenantID"),
                      ConfigHelper.GetVariable<string>("KeyVaultCertificateName"),
                      ConfigHelper.GetVariable<string>("PMETenantID"))
            );


        services.AddSingleton<KustoQueryExecutor>(new KustoQueryExecutor(
                    ConfigHelper.GetVariable<string>("SP_SUPPORTABILITY_CLIENT_ID"),
                    keyVaultService,
                    ConfigHelper.GetVariable<string>("LOGIN_ENDPOINT_URL"),
                    ConfigHelper.GetVariable<string>("TenantID"),
                    ConfigHelper.GetVariable<string>("KeyVaultCertificateName"),
                    ConfigHelper.GetVariable<string>("PMETenantID")
            )

            );
        
        if (environment == "Development" || environment == "PPE") //enable only on PPE and Dev
        {
            services.AddTransient<CommonRepositoryContext>();
        }

        services.AddSingleton<CaseAnalyzer>();

        services.AddScoped<ICaseLoader, CaseLoader>();
        services.AddSingleton<ICaseUploader, SearchFrontend.Endpoints.GraphRAG.CaseUploader>();


    })
    .ConfigureLogging(logging =>
    {
        logging.Services.Configure<LoggerFilterOptions>(options =>
        {
            LoggerFilterRule defaultRule = options.Rules.FirstOrDefault(rule => rule.ProviderName
                == "Microsoft.Extensions.Logging.ApplicationInsights.ApplicationInsightsLoggerProvider");
            if (defaultRule is not null)
            {
                options.Rules.Remove(defaultRule);
            }
            var logLevel = ConfigHelper.GetVariable("LOG_LEVEL", "INFO");
            // Add custom rule to capture Debug logs
            options.Rules.Add(new LoggerFilterRule(
                "Microsoft.Extensions.Logging.ApplicationInsights.ApplicationInsightsLoggerProvider",
                null,  // Apply to all categories
                (logLevel.Equals("DEBUG") ? Microsoft.Extensions.Logging.LogLevel.Debug : logLevel.Equals("INFO") ? Microsoft.Extensions.Logging.LogLevel.Information : logLevel.Equals("WARM") ? Microsoft.Extensions.Logging.LogLevel.Warning : logLevel.Equals("ERROR") ? Microsoft.Extensions.Logging.LogLevel.Error : Microsoft.Extensions.Logging.LogLevel.Information),  // Set minimum level to Debug
                null)); // No filter function
        });
    })
    .Build();

host.Run();
