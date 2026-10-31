using Autofac;
using SportsModels.Data.Basketball.Application;
using SportsModels.Data.Basketball.Contracts;
using SportsModels.Data.Basketball.Infrastructure;
using SportsModels.Data.NbaOfficial.Application;
using SportsModels.Data.NbaOfficial.Contracts;
using SportsModels.Data.NbaOfficial.Infrastructure;
using SportsModels.Data.NbaOfficial.Interfaces;

namespace SportsModels.Data.NbaOfficial.Autofac;

public sealed class ConversionModule(string registryPath) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder.Register(_ => new SqliteIdentityRegistry(registryPath)).As<IIdentityRegistry>().SingleInstance();
        builder.RegisterType<BasketballValidator>().As<IBasketballValidator>();
        builder.RegisterType<ConversionWorkspace>().As<IConversionWorkspace>();
        builder.RegisterType<NbaConversion>().As<INbaConversion>();
    }
}
