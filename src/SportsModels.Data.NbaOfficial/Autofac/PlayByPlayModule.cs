using Autofac;
using SportsModels.Data.NbaOfficial.Application;
using SportsModels.Data.NbaOfficial.Contracts;
using SportsModels.Data.NbaOfficial.Infrastructure;
using SportsModels.Data.NbaOfficial.Interfaces;

namespace SportsModels.Data.NbaOfficial.Autofac;

public sealed class PlayByPlayModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder.Register(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(30) }).SingleInstance();
        builder.RegisterType<NbaHttpSource>().As<INbaSource>();
        builder.RegisterType<RawJsonStore>().As<IRawJsonStore>();
        builder.RegisterType<PlayByPlayIngestion>().As<IPlayByPlayIngestion>();
        builder.RegisterType<NbaReferenceSource>().As<INbaReferenceSource>();
        builder.RegisterType<ReferenceStore>().As<IReferenceStore>();
        builder.RegisterType<ReferenceAcquisition>().As<INbaReferenceAcquisition>();
        builder.RegisterType<LocalGameInventoryReader>().As<ILocalGameInventory>();
    }
}
