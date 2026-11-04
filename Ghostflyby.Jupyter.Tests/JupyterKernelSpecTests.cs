using FluentAssertions;
using Ghostflyby.Jupyter.Client;

namespace Ghostflyby.Jupyter.Tests;

public sealed class JupyterKernelSpecTests
{
    [Fact]
    public async Task ReadAsyncProjectsEveryKernelSpecField()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jupyter-kernelspec-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(
                path,
                """
                {
                  "argv": ["python", "-m", "ipykernel_launcher", "-f", "{connection_file}"],
                  "display_name": "Python 3 (ipykernel)",
                  "language": "python",
                  "interrupt_mode": "message",
                  "env": { "TOKEN": "value" }
                }
                """,
                TestContext.Current.CancellationToken);

            var spec = await JupyterKernelSpec.ReadAsync(path, TestContext.Current.CancellationToken);

            spec.Argv.Should().Equal("python", "-m", "ipykernel_launcher", "-f", "{connection_file}");
            spec.DisplayName.Should().Be("Python 3 (ipykernel)");
            spec.Language.Should().Be("python");
            spec.InterruptMode.Should().Be("message");
            spec.Environment.Should().Contain("TOKEN", "value");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
