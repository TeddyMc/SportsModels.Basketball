using Autofac;
using SportsModels.Basketball.Data.Application;
using SportsModels.Basketball.Data.Contracts;
using SportsModels.Basketball.Data.Infrastructure;
using SportsModels.Basketball.Data.Interfaces;

namespace SportsModels.Basketball.Data.Autofac;

public sealed class PlayByPlayModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder.Register(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(30) }).SingleInstance();
        builder.RegisterType<NbaHttpSource>().As<INbaSource>();
        builder.RegisterType<RawJsonStore>().As<IRawJsonStore>();
        builder.RegisterType<PlayByPlayIngestion>().As<IPlayByPlayIngestion>();
    }
}
