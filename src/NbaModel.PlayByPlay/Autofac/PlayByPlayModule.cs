using Autofac;
using NbaModel.PlayByPlay.Application;
using NbaModel.PlayByPlay.Contracts;
using NbaModel.PlayByPlay.Infrastructure;
using NbaModel.PlayByPlay.Interfaces;

namespace NbaModel.PlayByPlay.Autofac;

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
