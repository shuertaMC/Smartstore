using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Moq;
using NUnit.Framework;
using Smartstore.Caching;
using Smartstore.Collections;
using Smartstore.Core.Data;
using Smartstore.Core.Identity;
using Smartstore.Core.Identity.Rules;
using Smartstore.Core.Rules;
using Smartstore.Core.Rules.Filters;
using Smartstore.Data;
using Smartstore.Data.Providers;
using Smartstore.Engine;
using Smartstore.Scheduling;
using Smartstore.Test.Common;
using Smartstore.Threading;

namespace Smartstore.Core.Tests.Platform.Identity.Rules;

[TestFixture]
public class TargetGroupEvaluatorTaskTests
{
    private SmartDbContext _db;
    private SqliteConnection _sqliteConnection;
    private Mock<ICacheManager> _cacheMock;
    private Mock<IRuleService> _ruleServiceMock;
    private Mock<IRuleProviderFactory> _ruleProviderFactoryMock;
    private Mock<ITargetGroupService> _targetGroupServiceMock;
    private Mock<ITaskStore> _taskStoreMock;
    private Mock<IAsyncState> _asyncStateMock;
    private Mock<IComponentContext> _componentContextMock;
    private TargetGroupEvaluatorTask _sut;

    private ILifetimeScope _container;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        // Set up a minimal EngineContext so that DbFactoryOptionsExtension can be
        // constructed without a full application host. An empty Autofac container
        // makes ResolveOptional<SmartConfiguration>() return null safely.
        var containerBuilder = new ContainerBuilder();
        _container = containerBuilder.Build();

        var mockAppContext = new Mock<IApplicationContext>();
        mockAppContext.Setup(x => x.Services).Returns(_container);

        var mockEngine = new Mock<IEngine>();
        mockEngine.Setup(x => x.Application).Returns(mockAppContext.Object);

        EngineContext.Replace(mockEngine.Object);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        EngineContext.Replace(null);
        _container?.Dispose();
        DataSettings.Reload();
    }

    [SetUp]
    public void SetUp()
    {
        // Use SQLite in-memory for each test. The InMemory provider does not support
        // ExecuteDeleteAsync which the SUT uses. SQLite in-memory requires an open
        // connection for the lifetime of the database.
        _sqliteConnection = new SqliteConnection("DataSource=:memory:");
        _sqliteConnection.Open();

        var dataSettings = new DataSettings
        {
            AppVersion = SmartstoreVersion.Version,
            ConnectionString = "Test",
            TenantName = "Default",
            TenantRoot = null,
            DbFactory = new SqliteTestDbFactory(_sqliteConnection)
        };

        DataSettings.Instance = dataSettings;
        DataSettings.SetTestMode(true);

        var builder = new DbContextOptionsBuilder<SmartDbContext>()
            .UseDbFactory(factoryBuilder =>
            {
                factoryBuilder.AddModelAssemblies(new[] { typeof(SmartDbContext).Assembly });
            });

        _db = new SmartDbContext((DbContextOptions<SmartDbContext>)builder.Options);
        _db.Database.EnsureCreated();

        // Set up mock dependencies.
        _cacheMock = new Mock<ICacheManager>();
        _cacheMock
            .Setup(x => x.RemoveByPatternAsync(It.IsAny<string>()))
            .ReturnsAsync(0L);

        _ruleServiceMock = new Mock<IRuleService>();

        _targetGroupServiceMock = new Mock<ITargetGroupService>();

        _ruleProviderFactoryMock = new Mock<IRuleProviderFactory>();
        _ruleProviderFactoryMock
            .Setup(x => x.GetProvider(RuleScope.Customer, null))
            .Returns(_targetGroupServiceMock.Object);

        _taskStoreMock = new Mock<ITaskStore>();
        _taskStoreMock
            .Setup(x => x.UpdateExecutionInfoAsync(It.IsAny<TaskExecutionInfo>()))
            .Returns(Task.CompletedTask);

        _asyncStateMock = new Mock<IAsyncState>();
        _asyncStateMock
            .Setup(x => x.GetAsync<TaskDescriptor>(It.IsAny<string>()))
            .Returns(Task.FromResult<TaskDescriptor>(null));

        _componentContextMock = new Mock<IComponentContext>();

        // Create the system under test.
        _sut = new TargetGroupEvaluatorTask(
            _db,
            _cacheMock.Object,
            _ruleServiceMock.Object,
            _ruleProviderFactoryMock.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _db?.Dispose();
        _sqliteConnection?.Dispose();
    }

    /// <summary>
    /// Creates a <see cref="TaskExecutionContext"/> with all required dependencies mocked.
    /// </summary>
    private TaskExecutionContext CreateTaskExecutionContext(IDictionary<string, string> parameters = null)
    {
        var executionInfo = new TaskExecutionInfo
        {
            Task = new TaskDescriptor
            {
                Name = "TargetGroupEvaluator",
                Type = nameof(TargetGroupEvaluatorTask)
            }
        };

        return new TaskExecutionContext(
            _taskStoreMock.Object,
            _asyncStateMock.Object,
            new DefaultHttpContext(),
            _componentContextMock.Object,
            executionInfo,
            parameters);
    }

    #region Test 1: Interface implementation

    [Test]
    public async Task Implements_ITask_interface()
    {
        // Assert that TargetGroupEvaluatorTask implements ITask.
        Assert.That(typeof(ITask).IsAssignableFrom(typeof(TargetGroupEvaluatorTask)), Is.True,
            "TargetGroupEvaluatorTask must implement ITask.");

        // Assert that Run can be invoked with a valid TaskExecutionContext without throwing.
        var ctx = CreateTaskExecutionContext();
        await _sut.Run(ctx, CancellationToken.None);
    }

    #endregion

    #region Test 2: No active roles

    [Test]
    public async Task Run_NoActiveRoles_CompletesWithoutErrorAndNoCacheInvalidation()
    {
        // Arrange: database has no customer roles at all.
        var ctx = CreateTaskExecutionContext();

        // Act: run the task to completion.
        await _sut.Run(ctx, CancellationToken.None);

        // Assert: cache was never invalidated because no mappings were added or deleted.
        _cacheMock.Verify(
            x => x.RemoveByPatternAsync(It.IsAny<string>()),
            Times.Never,
            "RemoveByPatternAsync should not be called when there are no active roles.");
    }

    #endregion

    #region Test 3: Rule sets produce no customers

    [Test]
    public async Task Run_RuleSetsProduceNoCustomers_CompletesGracefullyWithNoMappings()
    {
        // Arrange: seed a customer role with one active rule set.
        var ruleSet = new RuleSetEntity
        {
            IsActive = true,
            Scope = RuleScope.Customer
        };

        var role = new CustomerRole
        {
            Active = true,
            SystemName = "TestRole",
            Name = "Test Role"
        };

        // Add rule set to role via the navigation collection.
        role.RuleSets.Add(ruleSet);

        _db.CustomerRoles.Add(role);
        await _db.SaveChangesAsync();

        // Mock CreateExpressionGroupAsync to return a FilterExpressionGroup (which is a FilterExpression).
        var expressionGroup = new FilterExpressionGroup(typeof(Customer));
        _ruleServiceMock
            .Setup(x => x.CreateExpressionGroupAsync(
                It.IsAny<RuleSetEntity>(),
                It.IsAny<IRuleVisitor>(),
                It.IsAny<bool>()))
            .ReturnsAsync(expressionGroup);

        // Mock ProcessFilter to return an IPagedList<Customer> whose SourceQuery is an
        // EF Core-backed IQueryable that yields no customers. This is critical because
        // FastPager internally uses ToListAsync which requires an EF Core query provider.
        // No customers are seeded, so the base query naturally returns empty results.
        var emptyCustomerQuery = _db.Customers.AsNoTracking();
        var mockPagedList = new Mock<IPagedList<Customer>>();
        mockPagedList
            .Setup(x => x.SourceQuery)
            .Returns(emptyCustomerQuery);

        // The SUT calls ProcessFilter(expression, 0, 500) via an extension method that
        // wraps the single FilterExpression into an array and calls the interface method
        // ProcessFilter(FilterExpression[], LogicalRuleOperator, int, int).
        _targetGroupServiceMock
            .Setup(x => x.ProcessFilter(
                It.IsAny<FilterExpression[]>(),
                It.IsAny<LogicalRuleOperator>(),
                It.IsAny<int>(),
                It.IsAny<int>()))
            .Returns(mockPagedList.Object);

        var ctx = CreateTaskExecutionContext();

        // Act
        await _sut.Run(ctx, CancellationToken.None);

        // Assert: cache was never invalidated because no customer mappings were added.
        _cacheMock.Verify(
            x => x.RemoveByPatternAsync(It.IsAny<string>()),
            Times.Never,
            "RemoveByPatternAsync should not be called when no customer mappings are created.");

        // Assert: no CustomerRoleMapping records exist in the database.
        var mappingCount = await _db.CustomerRoleMappings.CountAsync();
        Assert.That(mappingCount, Is.EqualTo(0),
            "No CustomerRoleMapping records should be created when rule sets produce no customers.");

        // Assert: the rule service was called once (for the single active rule set).
        _ruleServiceMock.Verify(
            x => x.CreateExpressionGroupAsync(
                It.IsAny<RuleSetEntity>(),
                It.IsAny<IRuleVisitor>(),
                It.IsAny<bool>()),
            Times.Once,
            "CreateExpressionGroupAsync should be called exactly once for the single active rule set.");

        // Assert: ProcessFilter was called once (for the single expression group).
        _targetGroupServiceMock.Verify(
            x => x.ProcessFilter(
                It.IsAny<FilterExpression[]>(),
                It.IsAny<LogicalRuleOperator>(),
                It.IsAny<int>(),
                It.IsAny<int>()),
            Times.Once,
            "ProcessFilter should be called exactly once for the single expression group.");
    }

    #endregion
}
