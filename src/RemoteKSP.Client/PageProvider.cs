using System;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Abstractions;

namespace RemoteKSP.Client;

public class PageProvider : INavigationViewPageProvider
{
    private readonly IServiceProvider _serviceProvider;

    public PageProvider(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public object? GetPage(Type pageType)
    {
        return _serviceProvider.GetService(pageType);
    }
}
