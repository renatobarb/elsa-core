using System;
using System.Collections.Generic;
using System.Linq;
using Elsa.Models;
using Elsa.Services;
using Elsa.Services.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Elsa.Builders
{
    public class WorkflowBuilder : IWorkflowBuilder
    {
        private readonly IIdGenerator _idGenerator;
        private readonly IList<IActivityBuilder> _activityBuilders;
        private readonly IList<IConnectionBuilder> _connectionBuilders;

        public WorkflowBuilder(
            IIdGenerator idGenerator,
            IServiceProvider serviceProvider)
        {
            _idGenerator = idGenerator;
            ServiceProvider = serviceProvider;
            Id = idGenerator.Generate();
            Version = 1;
            _activityBuilders = new List<IActivityBuilder>();
            _connectionBuilders = new List<IConnectionBuilder>();
        }

        public IServiceProvider ServiceProvider { get; }
        public string Id { get; private set; }
        public string? Name { get; private set; }
        public string? Description { get; private set; }
        public int Version { get; private set; }
        public bool IsSingleton { get; private set; }
        public WorkflowPersistenceBehavior PersistenceBehavior { get; private set; }
        public bool DeleteCompletedInstances { get; private set; }

        public IWorkflowBuilder WithId(string value)
        {
            Id = value;
            return this;
        }

        public IWorkflowBuilder WithName(string? value)
        {
            Name = value;
            return this;
        }

        public IWorkflowBuilder WithDescription(string? value)
        {
            Description = value;
            return this;
        }

        public IWorkflowBuilder WithVersion(int value)
        {
            Version = value;
            return this;
        }

        public IWorkflowBuilder AsSingleton()
        {
            IsSingleton = true;
            return this;
        }

        public IWorkflowBuilder AsTransient()
        {
            IsSingleton = false;
            return this;
        }

        public IWorkflowBuilder WithPersistenceBehavior(WorkflowPersistenceBehavior value)
        {
            PersistenceBehavior = value;
            return this;
        }

        public IWorkflowBuilder WithDeleteCompletedInstances(bool value)
        {
            DeleteCompletedInstances = value;
            return this;
        }

        public IActivityBuilder New(
            Type activityType,
            Action<IActivity>? setupActivity = default,
            Action<IActivityBuilder>? branch = default,
            IDictionary<string, IActivityPropertyValueProvider>? propertyValueProviders = default)
        {
            var activityBuilder = new ActivityBuilder(activityType, setupActivity, this, propertyValueProviders);
            branch?.Invoke(activityBuilder);
            return activityBuilder;
        }

        public IActivityBuilder New<T>(
            Action<IActivityBuilder>? branch = default,
            IDictionary<string, IActivityPropertyValueProvider>? propertyValueProviders = default)
            where T : class, IActivity =>
            New(typeof(T), null, branch, propertyValueProviders);

        public IActivityBuilder New<T>(
            Action<ISetupActivity<T>>? setup = default,
            Action<IActivityBuilder>? branch = default) where T : class, IActivity
        {
            var propertyValuesBuilder = new SetupActivity<T>();
            setup?.Invoke(propertyValuesBuilder);

            var valueProviders = propertyValuesBuilder.ValueProviders.ToDictionary(
                x => x.Key,
                x => (IActivityPropertyValueProvider)new DelegateActivityPropertyValueProvider(x.Value));

            return New<T>(branch, valueProviders);
        }

        public IActivityBuilder New<T>(
            Action<T>? setup,
            Action<IActivityBuilder>? branch = default) where T : class, IActivity =>
            New(typeof(T), x => setup?.Invoke((T)x), branch);

        public IActivityBuilder StartWith<T>(
            Action<ISetupActivity<T>>? setup = default,
            Action<IActivityBuilder>? branch = default) where T : class, IActivity
        {
            var activityBuilder = New(setup, branch);
            return Add(activityBuilder, branch);
        }

        public IActivityBuilder StartWith<T>(
            Action<T>? setup,
            Action<IActivityBuilder>? branch = default) where T : class, IActivity
        {
            var activityBuilder = New(setup, branch);
            return Add(activityBuilder, branch);
        }

        public IActivityBuilder StartWith<T>(Action<IActivityBuilder>? branch = default)
            where T : class, IActivity =>
            Add<T>(branch);

        public IActivityBuilder Add<T>(
            Action<ISetupActivity<T>>? setup = default,
            Action<IActivityBuilder>? branch = default) where T : class, IActivity
        {
            var activityBuilder = New(setup, branch);
            return Add(activityBuilder, branch);
        }

        public IActivityBuilder Add<T>(
            Action<T> setup,
            Action<IActivityBuilder>? branch = default)
            where T : class, IActivity
        {
            var activityBuilder = New(setup, branch);
            return Add(activityBuilder, branch);
        }

        public IActivityBuilder Add<T>(
            Action<IActivityBuilder>? branch = default,
            IDictionary<string, IActivityPropertyValueProvider>? propertyValueProviders = default)
            where T : class, IActivity
        {
            var activityBuilder = new ActivityBuilder(typeof(T), null, this, propertyValueProviders);
            return Add(activityBuilder);
        }

        public IActivityBuilder Add(
            IActivityBuilder activityBuilder,
            Action<IActivityBuilder>? branch = default)
        {
            branch?.Invoke(activityBuilder);
            _activityBuilders.Add(activityBuilder);
            return activityBuilder;
        }

        public IConnectionBuilder Connect(
            IActivityBuilder source,
            IActivityBuilder target,
            string outcome = OutcomeNames.Done)
        {
            var connectionBuilder = new ConnectionBuilder(this, source, target, outcome);
            _connectionBuilders.Add(connectionBuilder);
            return connectionBuilder;
        }

        public IActivityBuilder Then<T>(
            Action<ISetupActivity<T>>? setup = default,
            Action<IActivityBuilder>? branch = default)
            where T : class, IActivity => StartWith(setup, branch);

        public IActivityBuilder Then<T>(
            Action<T>? setup = default,
            Action<IActivityBuilder>? branch = default)
            where T : class, IActivity => StartWith(setup, branch);

        public IActivityBuilder Then<T>(Action<IActivityBuilder>? branch = default)
            where T : class, IActivity => StartWith<T>(branch);

        public IWorkflowBlueprint Build(IWorkflow workflow)
        {
            WithId(workflow.GetType().Name);
            workflow.Build(this);
            return Build();
        }

        public IWorkflowBlueprint Build()
        {
            var definitionId = !string.IsNullOrWhiteSpace(Id) ? Id : _idGenerator.Generate();

            var builtActivities = _activityBuilders
                .Select(
                    activityBuilder => (activityBuilder, new ActivityBlueprint(activityBuilder.BuildActivityAsync())))
                .ToDictionary(x => x.activityBuilder);

            var activities = builtActivities.Select(x => x.Value.Item2).ToList();
            
            var connections = _connectionBuilders.Select(
                    x => new Connection(builtActivities[x.Source].Item2, builtActivities[x.Target].Item2, x.Outcome))
                .ToList();

            // Generate deterministic activity ids.
            var id = 1;

            foreach (var activity in activities.Where(activity => string.IsNullOrEmpty(activity.Id)))
                activity.Id = $"activity-{id++}";

            var activityPropertyValueProviders = _activityBuilders
                .Select(x => (x.ActivityId, x.PropertyValueProviders))
                .ToDictionary(x => x.ActivityId!, x => x.PropertyValueProviders!);

            var workflow = new WorkflowBlueprint(
                definitionId,
                Version,
                IsSingleton,
                false,
                Name,
                Description,
                true,
                true,
                PersistenceBehavior,
                DeleteCompletedInstances,
                activities,
                connections,
                new ActivityPropertyProviders(activityPropertyValueProviders));


            return workflow;
        }

        public IWorkflowBlueprint Build(Type workflowType)
        {
            var workflow = (IWorkflow)ActivatorUtilities.GetServiceOrCreateInstance(ServiceProvider, workflowType);
            return Build(workflow);
        }

        public IWorkflowBlueprint Build<T>() where T : IWorkflow => Build(typeof(T));
    }
}