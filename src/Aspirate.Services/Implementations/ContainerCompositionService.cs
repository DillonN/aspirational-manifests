namespace Aspirate.Services.Implementations;

public sealed class ContainerCompositionService(
    IFileSystem filesystem,
    IAnsiConsole console,
    IProjectPropertyService projectPropertyService,
    IShellExecutionService shellExecutionService) : IContainerCompositionService
{
    public async Task<bool> BuildAndPushContainerForProject(
        Project project,
        MsBuildContainerProperties containerDetails,
        bool nonInteractive = false)
    {
        var fullProjectPath = filesystem.NormalizePath(project.Path);

        var argumentsBuilder = ArgumentsBuilder.Create();

        await AddProjectPublishArguments(argumentsBuilder, fullProjectPath);
        AddContainerDetailsToArguments(argumentsBuilder, containerDetails);

        await shellExecutionService.ExecuteCommand(new()
        {
            Command = DotNetSdkLiterals.DotNetCommand,
            ArgumentsBuilder = argumentsBuilder,
            NonInteractive = nonInteractive,
            OnFailed = HandleBuildErrors,
            ShowOutput = true,
        });

        return true;
    }

    public async Task<bool> BuildAndPushContainerForDockerfile(Dockerfile dockerfile, string builder, string imageName, string? registry, bool nonInteractive)
    {
        var tagBuilder = new StringBuilder();
        var fullDockerfilePath = filesystem.GetFullPath(dockerfile.Path);

        if (!string.IsNullOrEmpty(registry))
        {
            tagBuilder.Append($"{registry}/");
        }

        tagBuilder.Append(imageName);
        tagBuilder.Append(":latest");

        var tag = tagBuilder.ToString();

        var argumentsBuilder = ArgumentsBuilder
            .Create()
            .AppendArgument(DockerLiterals.BuildCommand, string.Empty, quoteValue: false)
            .AppendArgument(DockerLiterals.TagArgument, tag)
            .AppendArgument(DockerLiterals.DockerFileArgument, fullDockerfilePath)
            .AppendArgument(dockerfile.Context, string.Empty, quoteValue: false);

        await shellExecutionService.ExecuteCommand(new()
        {
            Command = builder,
            ArgumentsBuilder = argumentsBuilder,
            NonInteractive = nonInteractive,
            ShowOutput = true,
        });

        if (!string.IsNullOrEmpty(registry))
        {

            argumentsBuilder.Clear()
                .AppendArgument(DockerLiterals.PushCommand, string.Empty, quoteValue: false)
                .AppendArgument(tag, string.Empty, quoteValue: false);

            await shellExecutionService.ExecuteCommand(
                new()
                {
                    Command = builder, ArgumentsBuilder = argumentsBuilder, NonInteractive = nonInteractive, ShowOutput = true,
                });
        }

        return true;
    }

    private Task HandleBuildErrors(string command, ArgumentsBuilder argumentsBuilder, bool nonInteractive, string errors)
    {
        if (errors.Contains(DotNetSdkLiterals.DuplicateFileOutputError, StringComparison.OrdinalIgnoreCase))
        {
            return HandleDuplicateFilesInOutput(argumentsBuilder, nonInteractive);
        }

        if (errors.Contains(DotNetSdkLiterals.NoContainerRegistryAccess, StringComparison.OrdinalIgnoreCase))
        {
            return HandleNoDockerRegistryAccess(argumentsBuilder, nonInteractive);
        }

        if (errors.Contains(DotNetSdkLiterals.UnknownContainerRegistryAddress, StringComparison.OrdinalIgnoreCase))
        {
            console.MarkupLine($"\r\n[red bold]{DotNetSdkLiterals.UnknownContainerRegistryAddress}: Unknown container registry address, or container registry address not accessible.[/]");
            throw new ActionCausesExitException(1013);
        }

        throw new ActionCausesExitException(9999);
    }

    private Task HandleDuplicateFilesInOutput(ArgumentsBuilder argumentsBuilder, bool nonInteractive = false)
    {
        var shouldRetry = AskIfShouldRetryHandlingDuplicateFiles(nonInteractive);
        if (shouldRetry)
        {
            argumentsBuilder.AppendArgument(DotNetSdkLiterals.ErrorOnDuplicatePublishOutputFilesArgument, "false");

            return shellExecutionService.ExecuteCommand(new()
            {
                Command = DotNetSdkLiterals.DotNetCommand,
                ArgumentsBuilder = argumentsBuilder,
                NonInteractive = nonInteractive,
                OnFailed = HandleBuildErrors,
                ShowOutput = true,
            });
        }

        throw new ActionCausesExitException(9999);
    }

    private async Task HandleNoDockerRegistryAccess(ArgumentsBuilder argumentsBuilder, bool nonInteractive)
    {
        if (nonInteractive)
        {
            console.MarkupLine($"\r\n[red bold]{DotNetSdkLiterals.NoContainerRegistryAccess}: No access to container registry. Cannot attempt login in non interactive mode.[/]");
            throw new ActionCausesExitException(1000);
        }

        var shouldLogin = AskIfShouldLoginToDocker(nonInteractive);
        if (shouldLogin)
        {
            var credentials = GatherDockerCredentials();

            var loginResult = await shellExecutionService.ExecuteCommandWithEnvironmentNoOutput(ContainerBuilderLiterals.DockerLoginCommand, credentials);

            if (loginResult)
            {
                await shellExecutionService.ExecuteCommand(new()
                {
                    Command = DotNetSdkLiterals.DotNetCommand,
                    ArgumentsBuilder = argumentsBuilder,
                    NonInteractive = nonInteractive,
                    OnFailed = HandleBuildErrors,
                });
            }
        }
    }

    private bool AskIfShouldRetryHandlingDuplicateFiles(bool nonInteractive)
    {
        if (nonInteractive)
        {
            return true;
        }

        return console.Confirm(
            "\r\n[red bold]Implicitly, dotnet publish does not allow duplicate filenames to be output to the artefact directory at build time.\r\nWould you like to retry the build explicitly allowing them?[/]\r\n");
    }

    private bool AskIfShouldLoginToDocker(bool nonInteractive)
    {
        if (nonInteractive)
        {
            return false;
        }

        return console.Confirm(
            "\r\nWe could not access the container registry during build. Do you want to login to the registry and retry?\r\n");
    }

    private Dictionary<string, string?> GatherDockerCredentials()
    {
        console.WriteLine();
        var registry = console.Ask<string>("What's the registry [green]address[/]?");
        var username = console.Ask<string>("Enter [green]username[/]?");
        var password = console.Prompt(
            new TextPrompt<string>("Enter [green]password[/]?")
                .PromptStyle("red")
                .Secret());

        return new()
        {
            { "DOCKER_HOST", registry },
            { "DOCKER_USER", username },
            { "DOCKER_PASS", password },
        };
    }

    private async Task AddProjectPublishArguments(ArgumentsBuilder argumentsBuilder, string fullProjectPath)
    {
        var propertiesJson = await projectPropertyService.GetProjectPropertiesAsync(
            fullProjectPath,
            MsBuildPropertiesLiterals.PublishSingleFileArgument,
            MsBuildPropertiesLiterals.PublishTrimmedArgument);

        var msbuildProperties = JsonSerializer.Deserialize<MsBuildProperties<MsBuildPublishingProperties>>(propertiesJson ?? "{}");

        if (string.IsNullOrEmpty(msbuildProperties.Properties.PublishSingleFile))
        {
            msbuildProperties.Properties.PublishSingleFile = DotNetSdkLiterals.DefaultSingleFile;
        }

        if (string.IsNullOrEmpty(msbuildProperties.Properties.PublishTrimmed))
        {
            msbuildProperties.Properties.PublishTrimmed = DotNetSdkLiterals.DefaultPublishTrimmed;
        }

        argumentsBuilder
            .AppendArgument(DotNetSdkLiterals.PublishArgument, fullProjectPath)
            .AppendArgument(DotNetSdkLiterals.PublishProfileArgument, DotNetSdkLiterals.ContainerPublishProfile)
            .AppendArgument(DotNetSdkLiterals.PublishSingleFileArgument, msbuildProperties.Properties.PublishSingleFile)
            .AppendArgument(DotNetSdkLiterals.PublishTrimmedArgument, msbuildProperties.Properties.PublishTrimmed)
            .AppendArgument(DotNetSdkLiterals.SelfContainedArgument, DotNetSdkLiterals.DefaultSelfContained)
            .AppendArgument(DotNetSdkLiterals.OsArgument, DotNetSdkLiterals.DefaultOs)
            .AppendArgument(DotNetSdkLiterals.ArchArgument, DotNetSdkLiterals.DefaultArch);

    }

    private static void AddContainerDetailsToArguments(ArgumentsBuilder argumentsBuilder, MsBuildContainerProperties containerDetails)
    {
        if (!string.IsNullOrEmpty(containerDetails.ContainerRegistry))
        {
            argumentsBuilder.AppendArgument(DotNetSdkLiterals.ContainerRegistryArgument, containerDetails.ContainerRegistry);
        }

        if (!string.IsNullOrEmpty(containerDetails.ContainerRepository))
        {
            argumentsBuilder.AppendArgument(DotNetSdkLiterals.ContainerRepositoryArgument, containerDetails.ContainerRepository);
        }

        if (!string.IsNullOrEmpty(containerDetails.ContainerImageName))
        {
            argumentsBuilder.AppendArgument(DotNetSdkLiterals.ContainerImageNameArgument, containerDetails.ContainerImageName);
        }

        argumentsBuilder.AppendArgument(DotNetSdkLiterals.ContainerImageTagArgument, containerDetails.ContainerImageTag);
    }
}