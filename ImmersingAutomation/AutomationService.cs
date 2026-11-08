using ImmersingAutomation.Abstractions;
using ImmersingAutomation.Models;
using Microsoft.Extensions.DependencyInjection;

namespace ImmersingAutomation;

public class AutomationService
{
    private IServiceCollection _serviceCollection = new ServiceCollection();

    private IServiceProvider _serviceProvider;
    
    private IAutomationStorageService _storageService;

    private List<Workflow> _workflows = [];

    public bool IsInitialized { get; private set; } = false;

    public IServiceCollection? ServiceCollection
    {
        get => !IsInitialized ? _serviceCollection : null;
        set => _serviceCollection = value ?? new ServiceCollection();
    }

    public IServiceProvider? ServiceProvider
    {
        get => IsInitialized ? _serviceProvider : null;
        set
        {
            if (value is null)
            {
                return;
            }

            _serviceProvider = value;
            IsInitialized = true;
        }
    }

    internal IEnumerable<ServiceDescriptor> ServiceDescriptors => _serviceCollection;

    public AutomationService(IAutomationStorageService storageService)
    {
        _storageService = storageService;
    }

    public async Task InitializeAsync()
    {
        _serviceProvider = _serviceCollection.BuildServiceProvider();
        _workflows = (await _storageService.LoadWorkflowsAsync()).ToList();
        IsInitialized = true;
    }
}
