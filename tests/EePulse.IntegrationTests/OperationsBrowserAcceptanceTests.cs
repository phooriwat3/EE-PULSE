using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using EePulse.Domain.Agents;
using EePulse.Domain.Inventory;
using EePulse.Domain.Status;
using EePulse.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EePulse.IntegrationTests;

/// <summary>Opt-in browser acceptance: a real Release API, Vite proxy, and owned PostgreSQL.</summary>
public sealed class OperationsBrowserAcceptanceTests
{
    private static readonly DateTimeOffset SeedTime = new(2026, 9, 27, 8, 0, 0, TimeSpan.Zero);
    private static readonly string[] DeviceNames = ["North press", "South press", "North controller"];

    [Fact(Explicit = true)]
    public async Task RealBackendReadOnlyOperationsAcceptance()
    {
        var ct = TestContext.Current.CancellationToken;
        var evidence = Environment.GetEnvironmentVariable("UI_EVIDENCE")
            ?? throw new InvalidOperationException("Set UI_EVIDENCE to a dedicated artifact directory.");
        Directory.CreateDirectory(evidence);
        var repo = FindRepository();
        var apiDll = Path.Combine(repo, "src", "backend", "EePulse.Api", "bin", "Release", "net10.0", "EePulse.Api.dll");
        Assert.True(File.Exists(apiDll), "Build the Release API/integration project first.");
        Assert.True(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EE_PULSE_TEST_POSTGRES")),
            "This acceptance fixture requires its own disposable container, not external/shared PostgreSQL.");

        await CleanupPlan.RunAsync(async cleanup =>
        {
            var postgres = await PostgresTestDatabase.StartIsolatedAsync(
                database => cleanup.Own("PostgreSQL disposal", database), ct);
            // Keep the maintenance database accessible while the dedicated fixture database is denied.
            var databaseName = "ui_acceptance_" + Guid.NewGuid().ToString("N");
            var adminString = new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = "postgres", Pooling = false }.ConnectionString;
            await AdminAsync(adminString, $"CREATE DATABASE {databaseName}", ct);
            cleanup.Add("restore fixture database", token =>
                AdminAsync(adminString, $"ALTER DATABASE {databaseName} ALLOW_CONNECTIONS true", token));
            var connectionString = new NpgsqlConnectionStringBuilder(postgres.ConnectionString)
            {
                Database = databaseName, Pooling = false, Timeout = 2, CommandTimeout = 3
            }.ConnectionString;
            await SeedAsync(connectionString, ct);

            var apiUrl = $"http://127.0.0.1:{FreePort()}";
            var webPort = FreePort();
            var controlToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var controlBuilder = WebApplication.CreateSlimBuilder();
            controlBuilder.Configuration["AllowedHosts"] = "127.0.0.1";
            controlBuilder.Logging.ClearProviders();
            controlBuilder.WebHost.UseUrls("http://127.0.0.1:0");
            var control = cleanup.Own("control host disposal", controlBuilder.Build());
            cleanup.Add("control host stop", control.StopAsync);
            control.MapPost("/{operation}", async (HttpContext context, string operation) =>
            {
                if (context.Request.Headers.Authorization != "Bearer " + controlToken) return Results.Unauthorized();
                var token = context.RequestAborted;
                switch (operation)
                {
                    case "database-off":
                        await AdminAsync(adminString, $"ALTER DATABASE {databaseName} ALLOW_CONNECTIONS false", token);
                        await CleanupPlan.RunAsync(async actionCleanup =>
                        {
                            var admin = actionCleanup.Own("outage connection disposal", new NpgsqlConnection(adminString));
                            await admin.OpenAsync(token);
                            var command = actionCleanup.Own("outage command disposal",
                                new NpgsqlCommand("SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @database", admin));
                            command.Parameters.AddWithValue("database", databaseName);
                            await command.ExecuteNonQueryAsync(token);
                        });
                        break;
                    case "database-on":
                        await AdminAsync(adminString, $"ALTER DATABASE {databaseName} ALLOW_CONNECTIONS true", token);
                        break;
                    case "rename":
                    case "reset":
                        await CleanupPlan.RunAsync(async actionCleanup =>
                        {
                            var db = actionCleanup.Own("rename context disposal", CreateDb(connectionString));
                            var device = await db.Devices.SingleAsync(row => row.Id == Id(10), token);
                            device.Update(device.SiteId, operation == "rename" ? "North press updated" : "North press",
                                device.Address, device.Hostname, device.DeviceType, device.Area, device.Owner,
                                device.Criticality, device.Tags, device.Enabled, SeedTime.AddMinutes(1));
                            await db.SaveChangesAsync(token);
                        });
                        break;
                    default: return Results.NotFound();
                }
                await File.AppendAllTextAsync(Path.Combine(evidence, "fixture-controls.jsonl"),
                    JsonSerializer.Serialize(new { operation, completedAt = DateTimeOffset.UtcNow }) + Environment.NewLine, token);
                return Results.NoContent();
            });
            await control.StartAsync(ct);
            var controlUrl = control.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();

            var api = await OwnedProcess.StartAsync("dotnet", [apiDll], Path.GetDirectoryName(apiDll)!, evidence, "real-api", cleanup,
                new Dictionary<string, string>
                {
                    ["ASPNETCORE_ENVIRONMENT"] = "Development", ["DOTNET_ENVIRONMENT"] = "Development",
                    ["ASPNETCORE_URLS"] = apiUrl, ["ConnectionStrings__Postgres"] = connectionString,
                    ["AllowedHosts"] = "127.0.0.1",
                    ["Inventory__SeedDevelopmentData"] = "false", ["IncidentCursor__ActiveKeyId"] = "acceptance",
                    ["IncidentCursor__KeyRing"] = "acceptance=" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                }, ct);
            await WaitForReadyAsync(apiUrl, api, ct);
            await File.WriteAllTextAsync(Path.Combine(evidence, "fixture.json"), JsonSerializer.Serialize(new
            {
                apiUrl, webPort, controlUrl, databaseName, seedTime = SeedTime,
                sites = new[] { Id(1), Id(2), Id(3) }, incidents = new[] { Id(30), Id(31) },
                environment = "Development", apiAssembly = apiDll
            }), ct);
            var web = Path.Combine(repo, "src", "web");
            var browser = await OwnedProcess.StartAsync("node",
                [Path.Combine(web, "node_modules", "@playwright", "test", "cli.js"), "test", "--config=playwright.real-backend.config.ts", "--workers=1"],
                web, evidence, "real-browser", cleanup, new Dictionary<string, string>
                {
                    ["UI_EVIDENCE"] = evidence, ["UI_REAL_API_URL"] = apiUrl,
                    ["UI_REAL_WEB_PORT"] = webPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["UI_REAL_CONTROL_URL"] = controlUrl, ["UI_REAL_CONTROL_TOKEN"] = controlToken
                }, ct);
            using var browserDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            browserDeadline.CancelAfter(TimeSpan.FromMinutes(5));
            Assert.Equal(0, await browser.WaitAsync(browserDeadline.Token));
            // The browser is read-only: test-controlled device renaming is the only intended mutation.
            var snapshot = cleanup.Own("persistence snapshot disposal", CreateDb(connectionString));
            Assert.Equal(2, await snapshot.AvailabilityIncidents.CountAsync(ct));
            Assert.All(await snapshot.AvailabilityIncidents.ToListAsync(ct), row => Assert.Equal(1, row.RowVersion));
            Assert.Equal(0, await snapshot.IncidentComments.CountAsync(ct));
            Assert.Equal(0, await snapshot.IncidentLifecycleActions.CountAsync(ct));
            Assert.Equal(0, await snapshot.IdempotencyReceipts.CountAsync(ct));
            await File.WriteAllTextAsync(Path.Combine(evidence, "read-only-persistence.json"),
                "{\"incidents\":2,\"incidentVersions\":[1,1],\"comments\":0,\"actions\":0,\"receipts\":0}", ct);
        });
    }

    [Fact]
    public async Task CleanupPrimaryFailurePrecedesDisposalFailureAndRetainsStack()
    {
        var primary = new InvalidOperationException("acceptance failed");
        var disposal = new IOException("disposal failed");
        var resource = new CleanupResource(disposal);
        var aggregate = await Assert.ThrowsAsync<AggregateException>(() => CleanupPlan.RunAsync(cleanup =>
        {
            cleanup.Own("resource", resource);
            return FailAcceptanceAsync(primary);
        }));
        Assert.Same(primary, aggregate.InnerExceptions[0]);
        Assert.Contains(nameof(FailAcceptanceAsync), primary.StackTrace);
        Assert.Same(disposal, aggregate.InnerExceptions[1].InnerException);
        Assert.Equal(1, resource.DisposalCount);
    }

    [Fact]
    public async Task CleanupDisposalFailureFailsSuccessfulAcceptance()
    {
        var disposal = new IOException("disposal failed");
        var resource = new CleanupResource(disposal);
        var accepted = false;
        var aggregate = await Assert.ThrowsAsync<AggregateException>(() => CleanupPlan.RunAsync(cleanup =>
        {
            cleanup.Own("resource", resource);
            accepted = true;
            return Task.CompletedTask;
        }));
        Assert.True(accepted);
        Assert.Same(disposal, Assert.Single(aggregate.InnerExceptions).InnerException);
        Assert.Equal(1, resource.DisposalCount);
    }

    [Fact]
    public async Task CleanupPartialStartupContinuesAfterDisposalFailure()
    {
        var startup = new InvalidOperationException("partial startup failed");
        var disposal = new IOException("disposal failed");
        var earlier = new CleanupResource();
        var partial = new CleanupResource(disposal);
        var unacquired = new CleanupResource();
        var tokens = new List<CancellationToken>();
        var aggregate = await Assert.ThrowsAsync<AggregateException>(() => CleanupPlan.RunAsync(async cleanup =>
        {
            cleanup.Own("earlier resource", earlier);
            cleanup.Add("continued cleanup", token =>
            {
                Assert.False(token.IsCancellationRequested);
                tokens.Add(token);
                return Task.CompletedTask;
            });
            cleanup.Own("partially started resource", partial);
            cleanup.Add("failed bounded step", token =>
            {
                tokens.Add(token);
                return Task.FromException(new OperationCanceledException(token));
            });
            await FailAcceptanceAsync(startup);
            cleanup.Own("never acquired", unacquired);
        }));
        Assert.Same(startup, aggregate.InnerExceptions[0]);
        Assert.Contains(nameof(FailAcceptanceAsync), startup.StackTrace);
        Assert.IsType<OperationCanceledException>(aggregate.InnerExceptions[1].InnerException);
        Assert.Same(disposal, aggregate.InnerExceptions[2].InnerException);
        Assert.Equal(1, earlier.DisposalCount);
        Assert.Equal(1, partial.DisposalCount);
        Assert.Equal(0, unacquired.DisposalCount);
        Assert.Equal(2, tokens.Count);
        Assert.NotEqual(tokens[0], tokens[1]);
    }

    [Fact]
    public async Task CleanupPrimaryFailureWithoutCleanupFailureRetainsOriginalException()
    {
        var primary = new InvalidOperationException("acceptance failed");
        var resource = new CleanupResource();
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => CleanupPlan.RunAsync(cleanup =>
        {
            cleanup.Own("resource", resource);
            return FailAcceptanceAsync(primary);
        }));
        Assert.Same(primary, thrown);
        Assert.Contains(nameof(FailAcceptanceAsync), thrown.StackTrace);
        Assert.Equal(1, resource.DisposalCount);
    }

    private static async Task FailAcceptanceAsync(Exception exception)
    {
        await Task.Yield();
        throw exception;
    }

    [Fact]
    public async Task CleanupDeadlineBoundsGatedSynchronousInvocationAndObservesLateFault()
    {
        var clock = new ManualCleanupTime();
        var budget = TimeSpan.FromMilliseconds(1);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leftInvocation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateFault = new IOException("late synchronous disposal fault");
        var gated = new CleanupResource(lateFault);
        var following = new CleanupResource();
        var unrelated = new CleanupResource();
        var primary = new InvalidOperationException("original acceptance failed");
        var invocations = 0;
        CleanupDeadlineFailure? timedOut = null;
        Exception? observed = null;
        var run = CleanupPlan.RunAsync(cleanup =>
        {
            cleanup.Own("following resource", following);
            cleanup.Add("gated synchronous resource", _ =>
            {
                Interlocked.Increment(ref invocations);
                entered.SetResult();
                try
                {
                    release.Wait(CancellationToken.None); // Intentionally blocks before returning a task.
                    return gated.DisposeAsync().AsTask();
                }
                finally { leftInvocation.SetResult(); }
            });
            return FailAcceptanceAsync(primary);
        }, clock, budget);
        try
        {
            // Real-time waits are safety guards only; deadline expiry is virtual and gated.
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            clock.Advance(budget);
            var aggregate = await Assert.ThrowsAsync<AggregateException>(() => run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.Same(primary, aggregate.InnerExceptions[0]);
            timedOut = Assert.IsType<CleanupDeadlineFailure>(aggregate.InnerExceptions[1].InnerException);
            Assert.Contains("gated synchronous resource", timedOut.Message);
            Assert.Contains(nameof(FailAcceptanceAsync), primary.StackTrace);
            Assert.Equal(1, following.DisposalCount);
            Assert.Equal(0, gated.DisposalCount);
            Assert.False(leftInvocation.Task.IsCompleted);
            Assert.False(timedOut.EventualCompletion.IsCompleted);
        }
        finally
        {
            release.Set();
            await leftInvocation.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            await JoinCleanupTestRunAsync(run);
            if (timedOut is not null)
                observed = await timedOut.EventualCompletion.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        }
        Assert.Same(lateFault, observed);
        Assert.Equal(1, invocations);
        Assert.Equal(1, gated.DisposalCount);
        Assert.Equal(1, following.DisposalCount);
        Assert.Equal(0, unrelated.DisposalCount);
    }

    [Fact]
    public async Task CleanupDeadlineBoundsReturnedAsyncTaskAndObservesLateFault()
    {
        var clock = new ManualCleanupTime();
        var budget = TimeSpan.FromMilliseconds(1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateFault = new IOException("late asynchronous disposal fault");
        var following = new CleanupResource();
        var invocations = 0;
        CleanupDeadlineFailure? timedOut = null;
        Exception? observed = null;
        var run = CleanupPlan.RunAsync(cleanup =>
        {
            cleanup.Own("following resource", following);
            cleanup.Add("pending asynchronous resource", _ =>
            {
                Interlocked.Increment(ref invocations);
                entered.SetResult();
                return completion.Task;
            });
            return Task.CompletedTask;
        }, clock, budget);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            clock.Advance(budget);
            var aggregate = await Assert.ThrowsAsync<AggregateException>(() => run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            timedOut = Assert.IsType<CleanupDeadlineFailure>(Assert.Single(aggregate.InnerExceptions).InnerException);
            Assert.Contains("pending asynchronous resource", timedOut.Message);
            Assert.Equal(1, following.DisposalCount);
            Assert.False(timedOut.EventualCompletion.IsCompleted);
        }
        finally
        {
            if (timedOut is not null)
            {
                completion.TrySetException(lateFault);
                observed = await timedOut.EventualCompletion.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            }
            else completion.TrySetResult();
            await JoinCleanupTestRunAsync(run);
        }
        Assert.Same(lateFault, observed);
        Assert.Equal(1, invocations);
        Assert.Equal(1, following.DisposalCount);
    }

    private static async Task JoinCleanupTestRunAsync(Task run)
    {
        try { await run.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None); }
        catch (AggregateException aggregate)
        {
            // The test body asserts the aggregate. Also observe/join it and all timed-out
            // workers in finally if that assertion or caller cancellation interrupted it.
            foreach (var failure in aggregate.InnerExceptions)
                if (failure.InnerException is CleanupDeadlineFailure timeout)
                    await timeout.EventualCompletion.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        }
    }

    [Fact]
    public async Task CleanupNormalCompletionRemainsSequential()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var following = new CleanupResource();
        var run = CleanupPlan.RunAsync(cleanup =>
        {
            cleanup.Own("following resource", following);
            cleanup.Add("first resource", _ =>
            {
                entered.SetResult();
                return completion.Task;
            });
            return Task.CompletedTask;
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(0, following.DisposalCount);
            Assert.False(run.IsCompleted);
        }
        finally
        {
            completion.TrySetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        }
        Assert.Equal(1, following.DisposalCount);
    }

    // Test-only virtual time: no sleeps, real 15-second wait, or external clock package.
    private sealed class ManualCleanupTime : TimeProvider
    {
        private readonly List<ManualTimer> _timers = new();
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            lock (_timers) _timers.Add(timer);
            return timer;
        }

        internal void Advance(TimeSpan amount)
        {
            var now = Interlocked.Add(ref _ticks, amount.Ticks);
            ManualTimer[] snapshot;
            lock (_timers) snapshot = _timers.ToArray();
            foreach (var timer in snapshot) timer.FireIfDue(now);
        }

        private sealed class ManualTimer(ManualCleanupTime clock, TimerCallback callback, object? state) : ITimer
        {
            private long _dueAt;
            private int _disposed;
            private int _fired;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (Volatile.Read(ref _disposed) != 0) return false;
                if (period != Timeout.InfiniteTimeSpan) throw new NotSupportedException("Only one-shot cleanup deadlines are supported.");
                Interlocked.Exchange(ref _dueAt, dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.GetTimestamp() + dueTime.Ticks);
                Interlocked.Exchange(ref _fired, 0);
                return true;
            }

            internal void FireIfDue(long now)
            {
                if (Volatile.Read(ref _disposed) == 0 && now >= Interlocked.Read(ref _dueAt)
                    && Interlocked.Exchange(ref _fired, 1) == 0) callback(state);
            }

            public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class CleanupResource(Exception? failure = null) : IAsyncDisposable
    {
        internal int DisposalCount { get; private set; }
        public ValueTask DisposeAsync()
        {
            DisposalCount++;
            return failure is null ? ValueTask.CompletedTask : ValueTask.FromException(failure);
        }
    }

    // Register each acquired resource before its next fallible operation. LIFO teardown
    // gives every step its own deadline, so one failure/cancellation cannot skip later steps.
    private sealed class CleanupPlan
    {
        private readonly Stack<(string Name, Func<CancellationToken, Task> Action)> _steps = new();

        internal void Add(string name, Func<CancellationToken, Task> action) => _steps.Push((name, action));

        internal T Own<T>(string name, T resource) where T : IAsyncDisposable
        {
            Add(name, _ => resource.DisposeAsync().AsTask());
            return resource;
        }

        internal static async Task RunAsync(Func<CleanupPlan, Task> acceptance,
            TimeProvider? timeProvider = null, TimeSpan? stepTimeout = null)
        {
            var cleanup = new CleanupPlan();
            ExceptionDispatchInfo? primary = null;
            try { await acceptance(cleanup); }
            catch (Exception exception) { primary = ExceptionDispatchInfo.Capture(exception); }

            var failures = new List<Exception>();
            while (cleanup._steps.TryPop(out var step))
            {
                var budget = stepTimeout ?? TimeSpan.FromSeconds(15);
                var deadline = new CancellationTokenSource(budget, timeProvider ?? TimeProvider.System);
                var token = deadline.Token;
                // Capture this popped action, not the mutable loop variable. Dispatch
                // invocation as well as its returned task off the cleanup coordinator.
                var action = step.Action;
                var work = Task.Run(() => action(token), CancellationToken.None);
                var observationOwnsDeadline = false;
                try { await work.WaitAsync(token); }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                {
                    // Timeout stops waiting, not the worker. Never retry or dispose the
                    // resource a second time; observe late completion/faults separately.
                    observationOwnsDeadline = true;
                    failures.Add(new InvalidOperationException($"Cleanup '{step.Name}' failed.",
                        new CleanupDeadlineFailure(step.Name, budget, work, deadline)));
                }
                catch (Exception exception)
                {
                    failures.Add(new InvalidOperationException($"Cleanup '{step.Name}' failed.", exception));
                }
                finally
                {
                    if (!observationOwnsDeadline) deadline.Dispose();
                }
            }
            if (failures.Count > 0)
            {
                if (primary is not null) failures.Insert(0, primary.SourceException);
                throw new AggregateException("Acceptance/cleanup failed; original failure is first when present.", failures);
            }
            primary?.Throw();
        }
    }

    private sealed class CleanupDeadlineFailure : TimeoutException
    {
        internal CleanupDeadlineFailure(string name, TimeSpan budget, Task work, CancellationTokenSource deadline)
            : base($"Cleanup '{name}' exceeded its {budget} deadline; its owned work may still be running.")
        {
            EventualCompletion = ObserveAsync(work, deadline);
        }

        // Always completes successfully with either null or the observed late fault.
        // Keeping the deadline alive permits late work to use its captured token safely.
        internal Task<Exception?> EventualCompletion { get; }

        private static async Task<Exception?> ObserveAsync(Task work, CancellationTokenSource deadline)
        {
            try { await work.ConfigureAwait(false); return null; }
            catch (Exception exception) { return exception; }
            finally { deadline.Dispose(); }
        }
    }

    private static Guid Id(int value) => Guid.Parse($"00000000-0000-4000-8000-{value:x12}");
    private static EePulseDbContext CreateDb(string connection) => new(new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(connection).Options);

    private static async Task SeedAsync(string connection, CancellationToken ct)
    {
        await CleanupPlan.RunAsync(async cleanup =>
        {
            var db = cleanup.Own("seed context disposal", CreateDb(connection));
            await db.Database.MigrateAsync(ct);
            db.AddRange(new Site(Id(1), "NORTH", "North assembly", "UTC", SeedTime),
                new Site(Id(2), "SOUTH", "South assembly", "UTC", SeedTime),
                new Site(Id(3), "EMPTY", "Empty acceptance site", "UTC", SeedTime),
                new AgentGroup(Id(4), "Acceptance agents", null, SeedTime));
            for (var index = 0; index < 3; index++)
            {
                var device = new Device(Id(10 + index), index == 1 ? Id(2) : Id(1),
                    DeviceNames[index], $"192.0.2.{10 + index}", null,
                    "PLC", "Acceptance line", null, Criticality.High, ["acceptance"], SeedTime);
                db.AddRange(device, new Probe(Id(20 + index), device.Id, Id(4), 60, 1_000, 1, null, null, 1, 1));
            }
            var agent = new EePulse.Domain.Agents.Agent(Id(40), Id(4), Id(41), "Acceptance offline agent", "1.0", 20, SeedTime);
            agent.Heartbeat("1.0", agent.MachineName, 0, AgentSelfHealth.Healthy, 0, SeedTime, SeedTime);
            agent.MarkOffline(SeedTime.AddHours(2));
            db.Add(agent);
            await db.SaveChangesAsync(ct);
            for (var index = 0; index < 2; index++)
            {
                var incident = new AvailabilityIncident(Id(30 + index), Id(20 + index), SeedTime.AddMinutes(-index));
                db.AddRange(incident, new ProbeStatusProjection(Id(20 + index), ProbeStatus.Down, 1, 0,
                    SeedTime, SeedTime, agent.Id, Id(50 + index), incident.Id));
            }
            db.Add(new ProbeStatusProjection(Id(22), ProbeStatus.Up, 0, 0, SeedTime, null, null, null));
            await db.SaveChangesAsync(ct);
        });
    }

    private static async Task AdminAsync(string connection, string sql, CancellationToken ct)
    {
        await CleanupPlan.RunAsync(async cleanup =>
        {
            var admin = cleanup.Own("admin connection disposal", new NpgsqlConnection(connection));
            await admin.OpenAsync(ct);
            var command = cleanup.Own("admin command disposal", new NpgsqlCommand(sql, admin));
            await command.ExecuteNonQueryAsync(ct);
        });
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "global.json"))) return directory.FullName;
        throw new InvalidOperationException("Run from the repository's built IntegrationTests executable.");
    }

    private static async Task WaitForReadyAsync(string url, OwnedProcess api, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        while (true)
        {
            Assert.False(api.HasExited, "Release API exited before readiness; inspect real-api.stderr/stdout logs.");
            try
            {
                using var response = await http.GetAsync(url + "/health/ready", deadline.Token);
                if (response.IsSuccessStatusCode) return;
                if (response.StatusCode != HttpStatusCode.ServiceUnavailable)
                    throw new InvalidOperationException($"Readiness returned {(int)response.StatusCode}; inspect Release API logs.");
            }
            catch (HttpRequestException) { /* Startup has not yet bound the socket. */ }
            catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { /* Per-probe timeout; overall readiness remains bounded. */ }
            await Task.Delay(200, deadline.Token);
        }
    }

    private sealed class OwnedProcess
    {
        private readonly Process _process;
        private Task _output = Task.CompletedTask;
        private Task _error = Task.CompletedTask;
        private readonly string _result;
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private bool _terminatedForCleanup;
        private bool _started;

        private OwnedProcess(Process process, string result)
        {
            _process = process; _result = result;
        }

        internal bool HasExited => _process.HasExited;

        internal static async Task<OwnedProcess> StartAsync(string executable, string[] arguments, string workingDirectory,
            string evidence, string name, CleanupPlan cleanup, Dictionary<string, string> environment, CancellationToken ct)
        {
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
            var stdout = cleanup.Own(name + " stdout disposal",
                new FileStream(Path.Combine(evidence, name + ".stdout.log"), FileMode.Create, FileAccess.Write, FileShare.Read));
            var stderr = cleanup.Own(name + " stderr disposal",
                new FileStream(Path.Combine(evidence, name + ".stderr.log"), FileMode.Create, FileAccess.Write, FileShare.Read));
            var process = new Process { StartInfo = start };
            cleanup.Add(name + " process handle disposal", _ =>
            {
                process.Dispose();
                return Task.CompletedTask;
            });
            var owned = new OwnedProcess(process, Path.Combine(evidence, name + ".result.json"));
            // Register before launch/log setup. Failure to start or write the PID cannot leak
            // streams or leave an untracked child; terminate/wait/result are separate steps.
            cleanup.Add(name + " result recording", owned.RecordResultAsync);
            cleanup.Add(name + " exit/output drain", token => owned._started ? owned.WaitAsync(token) : Task.CompletedTask);
            cleanup.Add(name + " termination", _ =>
            {
                if (owned._started && !process.HasExited)
                {
                    owned._terminatedForCleanup = true;
                    process.Kill(entireProcessTree: true);
                }
                return Task.CompletedTask;
            });
            owned._started = process.Start();
            if (!owned._started) throw new InvalidOperationException("Child process did not start.");
            // Keep draining after caller cancellation; termination and bounded drain
            // are independently owned by the cleanup plan, not the acceptance token.
            owned._output = process.StandardOutput.BaseStream.CopyToAsync(stdout, CancellationToken.None);
            owned._error = process.StandardError.BaseStream.CopyToAsync(stderr, CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(evidence, name + ".pid.json"),
                JsonSerializer.Serialize(new { pid = process.Id, executable, arguments, startedAt = DateTimeOffset.UtcNow }), ct);
            return owned;
        }

        internal async Task<int> WaitAsync(CancellationToken ct)
        {
            await _process.WaitForExitAsync(ct);
            await Task.WhenAll(_output, _error).WaitAsync(ct);
            return _process.ExitCode;
        }

        private Task RecordResultAsync(CancellationToken ct)
        {
            var exited = _started && _process.HasExited;
            return File.WriteAllTextAsync(_result, JsonSerializer.Serialize(new
            {
                pid = _started ? (int?)_process.Id : null, exitCode = exited ? (int?)_process.ExitCode : null,
                durationSeconds = _watch.Elapsed.TotalSeconds, terminatedForCleanup = _terminatedForCleanup,
                started = _started, exited
            }), ct);
        }
    }
}
