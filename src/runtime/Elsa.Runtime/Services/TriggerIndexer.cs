using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Elsa.Contracts;
using Elsa.Extensions;
using Elsa.Helpers;
using Elsa.Mediator.Contracts;
using Elsa.Models;
using Elsa.Persistence.Commands;
using Elsa.Persistence.Entities;
using Elsa.Runtime.Contracts;
using Elsa.Runtime.Notifications;
using Microsoft.Extensions.Logging;

namespace Elsa.Runtime.Services;

/// <summary>
/// Pre-indexes workflow triggers from providers that are static in nature.
/// These are providers such as the ConfigurationWorkflowProvider, whose set of workflows will never change after application has started.
/// Workflows stored in the DB, on the other hand, will be updated via API endpoints, which will then be indexed right there and then.
/// To prevent potentially loading hundreds of user-defined workflows from the DB, we will skip that provider. 
/// </summary>
public class TriggerIndexer : ITriggerIndexer
{
    private readonly IWorkflowRegistry _workflowRegistry;
    private readonly IExpressionEvaluator _expressionEvaluator;
    private readonly IIdentityGenerator _identityGenerator;
    private readonly ICommandSender _commandSender;
    private readonly IEventPublisher _eventPublisher;
    private readonly IServiceProvider _serviceProvider;
    private readonly IHasher _hasher;
    private readonly ILogger _logger;

    public TriggerIndexer(
        IWorkflowRegistry workflowRegistry,
        IExpressionEvaluator expressionEvaluator,
        IIdentityGenerator identityGenerator,
        ICommandSender commandSender,
        IEventPublisher eventPublisher,
        IServiceProvider serviceProvider,
        IHasher hasher,
        ILogger<TriggerIndexer> logger)
    {
        _workflowRegistry = workflowRegistry;
        _expressionEvaluator = expressionEvaluator;
        _identityGenerator = identityGenerator;
        _commandSender = commandSender;
        _eventPublisher = eventPublisher;
        _serviceProvider = serviceProvider;
        _hasher = hasher;
        _logger = logger;
    }

    public async Task IndexTriggersAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = new Stopwatch();

        _logger.LogInformation("Indexing workflow triggers");
        stopwatch.Start();

        // Only stream workflows from providers that are not "dynamic" (such as DatabaseWorkflowProvider).
        var workflows = _workflowRegistry.StreamAllAsync(WorkflowRegistry.SkipDynamicProviders, cancellationToken);
        //var collectedTriggers = new List<WorkflowTrigger>();

        await foreach (var workflow in workflows.WithCancellation(cancellationToken))
        {
            //var triggers = await GetTriggersAsync(workflow, cancellationToken).ToListAsync(cancellationToken);
            await IndexTriggersAsync(workflow, cancellationToken);
        }

        // // Replace triggers for the specified workflow.
        // await _commandSender.ExecuteAsync(new ReplaceWorkflowTriggers(collectedTriggers), cancellationToken);

        stopwatch.Stop();
        _logger.LogInformation("Finished indexing workflow triggers in {ElapsedTime}", stopwatch.Elapsed);

        // // Publish event.
        // await _eventPublisher.PublishAsync(new TriggerIndexingFinished(collectedTriggers), cancellationToken);
    }

    public async Task<IEnumerable<WorkflowTrigger>> IndexTriggersAsync(Workflow workflow, CancellationToken cancellationToken = default)
    {
        // Collect new triggers.
        var triggers = await GetTriggersAsync(workflow, cancellationToken).ToListAsync(cancellationToken);

        // Replace triggers for the specified workflow.
        await _commandSender.ExecuteAsync(new ReplaceWorkflowTriggers(workflow, triggers), cancellationToken);

        // Publish event.
        await _eventPublisher.PublishAsync(new TriggerIndexingFinished(triggers), cancellationToken);
        return triggers;
    }

    private async IAsyncEnumerable<WorkflowTrigger> GetTriggersAsync(Workflow workflow, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var context = new WorkflowIndexingContext(workflow);
        var triggerSources = workflow.Triggers;

        foreach (var triggerSource in triggerSources)
        {
            var triggers = await GetTriggersAsync(workflow, context, triggerSource, cancellationToken);

            foreach (var trigger in triggers)
                yield return trigger;
        }
    }

    private async Task<IEnumerable<WorkflowTrigger>> GetTriggersAsync(Workflow workflow, WorkflowIndexingContext context, ITrigger trigger, CancellationToken cancellationToken)
    {
        var inputs = trigger.GetInputs();
        var assignedInputs = inputs.Where(x => x.LocationReference != null!).ToList();
        var register = context.GetOrCreateRegister(trigger);
        var expressionExecutionContext = new ExpressionExecutionContext(_serviceProvider, register, default);

        // Evaluate trigger inputs.
        foreach (var input in assignedInputs)
        {
            var locationReference = input.LocationReference;

            try
            {
                var value = await _expressionEvaluator.EvaluateAsync(input, expressionExecutionContext);
                locationReference.Set(expressionExecutionContext, value);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Failed to evaluate '{@Expression}'", input.Expression);
            }
        }

        var triggerIndexingContext = new TriggerIndexingContext(context, expressionExecutionContext, trigger);
        var payloads = await TryGetPayloadsAsync(trigger, triggerIndexingContext, cancellationToken);
        var triggerType = trigger.GetType();
        var triggerTypeName = TypeNameHelper.GenerateTypeName(triggerType);

        var triggers = payloads.Select(x => new WorkflowTrigger
        {
            Id = _identityGenerator.GenerateId(),
            WorkflowDefinitionId = workflow.Identity.DefinitionId,
            Name = triggerTypeName,
            Hash = _hasher.Hash(x),
            Payload = JsonSerializer.Serialize(x)
        });

        return triggers;
    }

    private async Task<ICollection<object>> TryGetPayloadsAsync(ITrigger trigger, TriggerIndexingContext context, CancellationToken cancellationToken)
    {
        try
        {
            return (await trigger.GetPayloadsAsync(context, cancellationToken)).ToList();
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Failed to get hash inputs");
        }

        return Array.Empty<object>();
    }
}