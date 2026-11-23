// UseWPF=true を Microsoft.NET.Sdk.Web に追加すると
// WPF SDK の暗黙 using が Web SDK の暗黙 using を上書きするため、
// 不足した名前空間をここで明示的に再宣言する。
global using System.IO;
global using System.Net.Http;
global using System.Net.Http.Json;
global using Microsoft.AspNetCore.Builder;
global using Microsoft.AspNetCore.Hosting;
global using Microsoft.AspNetCore.Http;
global using Microsoft.AspNetCore.Routing;
global using Microsoft.Extensions.Configuration;
global using Microsoft.Extensions.DependencyInjection;
global using Microsoft.Extensions.Hosting;
global using Microsoft.Extensions.Logging;
global using MobMate.App.WebHost;
