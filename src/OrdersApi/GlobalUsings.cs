// Global using directives

global using Microsoft.AspNetCore.Builder;
global using Microsoft.AspNetCore.Http;
global using Microsoft.EntityFrameworkCore;
global using Microsoft.EntityFrameworkCore.Diagnostics;
global using Microsoft.EntityFrameworkCore.Storage;
global using Microsoft.Extensions.Configuration;
global using Microsoft.Extensions.DependencyInjection;
global using Microsoft.Extensions.Hosting;
global using Microsoft.Extensions.Logging;
global using Npgsql;
global using Npgsql.EntityFrameworkCore.PostgreSQL;
global using OrdersApi.Data;
global using OrdersApi.FaultInjection;
global using OrdersApi.Instrumentation;
global using ServiceDefaults;
global using ServiceDefaults.Configuration;
global using ServiceDefaults.Counting;
global using System;
global using System.Collections.Generic;
global using System.Data.Common;
global using System.Diagnostics.Metrics;
global using System.Threading;
global using System.Threading.Tasks;