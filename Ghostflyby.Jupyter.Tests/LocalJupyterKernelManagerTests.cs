using FluentAssertions;
using Ghostflyby.Jupyter.Client;

namespace Ghostflyby.Jupyter.Tests;

public sealed class LocalJupyterKernelManagerTests
{
    [Fact]
    public void OptionsExposeDefaultsAndConfiguredValues()
    {
        var defaults = new LocalJupyterKernelManagerOptions();

        defaults.WorkingDirectory.Should().BeNull();
        defaults.ClearInheritedEnvironment.Should().BeFalse();
        defaults.Environment.Should().BeEmpty();
        defaults.StartupTimeout.Should().Be(TimeSpan.FromSeconds(30));
        defaults.ShutdownTimeout.Should().Be(TimeSpan.FromSeconds(10));
        defaults.RuntimeDirectory.Should().BeNull();

        var environment = new Dictionary<string, string> { ["TOKEN"] = "value" };
        var configured = new LocalJupyterKernelManagerOptions
        {
            WorkingDirectory = "work",
            ClearInheritedEnvironment = true,
            Environment = environment,
            StartupTimeout = TimeSpan.FromSeconds(5),
            ShutdownTimeout = TimeSpan.FromSeconds(2),
            RuntimeDirectory = "runtime"
        };

        configured.WorkingDirectory.Should().Be("work");
        configured.ClearInheritedEnvironment.Should().BeTrue();
        configured.Environment.Should().BeSameAs(environment);
        configured.StartupTimeout.Should().Be(TimeSpan.FromSeconds(5));
        configured.ShutdownTimeout.Should().Be(TimeSpan.FromSeconds(2));
        configured.RuntimeDirectory.Should().Be("runtime");
    }

    [Fact]
    public void StartupExceptionExposesFailureDiagnostics()
    {
        var inner = new InvalidOperationException("inner");
        var exception = new JupyterKernelStartupException("test-kernel", 3, false, "stderr output", inner);

        exception.DisplayName.Should().Be("test-kernel");
        exception.ExitCode.Should().Be(3);
        exception.TimedOut.Should().BeFalse();
        exception.StandardError.Should().Be("stderr output");
        exception.InnerException.Should().BeSameAs(inner);
        exception.Message.Should().Contain("exited with code 3").And.Contain("stderr output");
    }
}
