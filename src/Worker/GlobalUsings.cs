// Global using directives

global using Azure.Messaging.ServiceBus;
global using Azure.Messaging.ServiceBus.Administration;
global using Microsoft.AspNetCore.Builder;
global using Microsoft.AspNetCore.Http;
global using Microsoft.Extensions.Configuration;
global using Microsoft.Extensions.DependencyInjection;
global using Microsoft.Extensions.Hosting;
global using Microsoft.Extensions.Http.Resilience;
global using Microsoft.Extensions.Logging;
global using ServiceDefaults;
global using ServiceDefaults.Configuration;
global using ServiceDefaults.Counting;
global using ServiceDefaults.Resilience;
global using System;
global using System.Collections.Concurrent;
global using System.Collections.Generic;
global using System.Diagnostics;
global using System.Diagnostics.Metrics;
global using System.Linq;
global using System.Net.Http;
global using System.Threading;
global using System.Threading.Tasks;
global using Worker.Clients;
global using Worker.Configuration;
global using Worker.Messaging;