using k8s.Models;
using System.Xml.Linq;

namespace Aspirate.Services.Implementations;

public class HelmChartCreator(IFileSystem fileSystem, IKubernetesService kubernetesService, IAnsiConsole logger) : IHelmChartCreator
{
    public async Task CreateHelmChart(List<object> kubernetesObjects, string chartPath, string chartName, bool includeDashboard)
    {
        if (fileSystem.Directory.Exists(chartPath))
        {
            fileSystem.Directory.Delete(chartPath, true);
        }

        fileSystem.Directory.CreateDirectory(chartPath);
        fileSystem.Directory.CreateDirectory(fileSystem.Path.Combine(chartPath, "templates"));

        if (includeDashboard)
        {
            CreateDashboardObjects(kubernetesObjects);
        }

        await ProcessObjects(kubernetesObjects, chartPath);

        await CreateChartFile(chartPath, chartName);

        logger.MarkupLine($"[green]({EmojiLiterals.CheckMark}) Done: [/] Generating helm chart at [blue]{chartPath}[/]");
    }

    private void CreateDashboardObjects(List<object> kubernetesObjects)
    {
        var dashboardObjects = kubernetesService.CreateDashboardKubernetesObjects();
        kubernetesObjects.AddRange(dashboardObjects);
    }

    private static async Task ProcessObjects(List<object> resources, string chartPath)
    {
        var values = new Dictionary<string, Dictionary<object, object>>();

        foreach (var resource in resources)
        {
            switch (resource)
            {
                case V1ConfigMap configMap:
                    configMap.Metadata.NamespaceProperty = null;
                    await HandleConfigMap(configMap, chartPath, values);
                    continue;
                case V1Secret secret:
                    secret.Metadata.NamespaceProperty = null;
                    await WriteResourceFile(KubernetesYaml.Serialize(secret), chartPath, secret.Metadata.Name, secret.Kind);
                    continue;
                case V1Deployment deployment:
                    deployment.Metadata.NamespaceProperty = null;
                    await HandleDeployment(deployment, chartPath, values);
                    continue;
                case V1StatefulSet statefulSet:
                    statefulSet.Metadata.NamespaceProperty = null;
                    await HandleStatefulSet(statefulSet, chartPath, values);
                    continue;
                case V1Service service:
                    service.Metadata.NamespaceProperty = null;
                    await WriteResourceFile(KubernetesYaml.Serialize(service), chartPath, service.Metadata.Name, service.Kind);
                    continue;
            }
        }

        await CreateValuesFile(values, chartPath);
    }

    private static Task HandleConfigMap(V1ConfigMap configMap, string chartPath, Dictionary<string, Dictionary<object, object>> values)
    {
        var metadata = configMap.Metadata;
        var name = metadata.Name;
        var kind = configMap.Kind;
        var formattedName = FormatKebabToCamel(name);

        if (!values.TryGetValue(formattedName, out var resourceValues))
        {
            resourceValues = [];
            values[formattedName] = resourceValues;
        }

        if (!resourceValues.TryGetValue("env", out var envValuesObj) ||
            envValuesObj is not Dictionary<string, string> envValues)
        {
            envValues = [];
            resourceValues["env"] = envValues;
        }

        var newData = new Dictionary<string, string>(configMap.Data.Count);

        foreach (var (key, value) in configMap.Data)
        {
            envValues[key] = value;

            newData[key] = $"{{{{ .Values.{formattedName}.env.{key} | default .Values.global.env.{key} }}}}";
        }

        if (envValues.Count == 0)
        {
            resourceValues.Remove("env");
        }

        if (resourceValues.Count == 0)
        {
            values.Remove(formattedName);
        }

        configMap.Data = newData;

        var udpatedResource = KubernetesYaml.Serialize(configMap);

        return WriteResourceFile(udpatedResource, chartPath, name, kind);
    }

    private static Task HandleStatefulSet(
        V1StatefulSet statefulSet,
        string chartPath,
        Dictionary<string, Dictionary<object, object>> values)
    {
        var metadata = statefulSet.Metadata;
        var name = metadata.Name;
        var kind = statefulSet.Kind;

        PopulateValuesImages(statefulSet.Spec.Template.Spec.Containers, values, name);

        var updatedResource = KubernetesYaml.Serialize(statefulSet);

        return WriteResourceFile(updatedResource, chartPath, name, kind);
    }

    private static Task HandleDeployment(
        V1Deployment deployment,
        string chartPath,
        Dictionary<string, Dictionary<object, object>> values)
    {
        var metadata = deployment.Metadata;
        var name = metadata.Name;
        var kind = deployment.Kind;

        PopulateValuesImages(deployment.Spec.Template.Spec.Containers, values, name);

        var updatedResource = KubernetesYaml.Serialize(deployment);

        return WriteResourceFile(updatedResource, chartPath, name, kind);
    }

    private static Task WriteResourceFile(string updatedResource, string chartPath, string name, string kind)
    {
        var filename = $"{chartPath}/templates/{name.ToLower()}-{kind.ToLower()}.yaml";

        return File.WriteAllTextAsync(filename, updatedResource);
    }

    private static void PopulateValuesImages(
        IEnumerable<V1Container>? containers,
        Dictionary<string, Dictionary<object, object>> values,
        string name)
    {
        var formattedName = FormatKebabToCamel(name);
        foreach (var container in containers)
        {
            var image = container.Image;
            var imageComponents = image.Split(':');
            if (imageComponents.Length == 0)
            {
                throw new Exception("Image not specified");
            }

            var imageRepo = imageComponents[0];
            var imageTag = imageComponents.Length > 1 ? imageComponents[1] : "latest";

            if (!values.TryGetValue(formattedName, out var resourceValues))
            {
                resourceValues = [];
                values[formattedName] = resourceValues;
            }

            if (!resourceValues.TryGetValue("image", out var imageValuesObj) ||
                imageValuesObj is not Dictionary<string, string> imageValues)
            {
                imageValues = [];
                resourceValues["image"] = imageValues;
            }

            imageValues["repository"] = imageRepo;
            imageValues["tag"] = imageTag;
            imageValues["pullPolicy"] = imageTag == "latest" ? "Always" : "IfNotPresent";

            container.Image = $"{{{{ .Values.{formattedName}.image.repository }}}}:{{{{ .Values.{formattedName}.image.tag }}}}";
            container.ImagePullPolicy = $"{{{{ .Values.{formattedName}.image.pullPolicy }}}}";
        }
    }

    private static async Task CreateChartFile(string chartPath, string chartName)
    {
        var serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        var chartFile = $"{chartPath}/Chart.yaml";

        var chartContent = new Dictionary<object, object>
        {
            { "apiVersion", "v2" },
            { "name", chartName },
            { "description", "A Helm chart to Deploy your Aspire Project to Kubernetes." },
            { "appVersion", "1.0.0" },
            { "version", "1.0.0" }
        };

        var yaml = serializer.Serialize(chartContent);

        await File.WriteAllTextAsync(chartFile, yaml);
    }

    private static async Task CreateValuesFile(Dictionary<string, Dictionary<object, object>> values, string chartPath)
    {
        var globalEnvValues = new Dictionary<string, string>();

        foreach (var (resourceName, resourceValues) in values)
        {
            if (!resourceValues.TryGetValue("env", out var envValuesObj) ||
                envValuesObj is not Dictionary<string, string> envValues)
            {
                continue;
            }

            var newData = new Dictionary<string, string>(envValues.Count);

            foreach (var (key, value) in envValues)
            {
                if (globalEnvValues.TryGetValue(key, out var globalValue))
                {
                    if (globalValue != value)
                    {
                        newData[key] = value;
                    }
                }
                else if (values.Where(v => v.Key != resourceName).Any(kvp =>
                {
                    if (!kvp.Value.TryGetValue("env", out var otherEnvValuesObj) ||
                        otherEnvValuesObj is not Dictionary<string, string> otherEnvValues)
                    {
                        return false;
                    }

                    return otherEnvValues.TryGetValue(key, out var otherValue) && otherValue == value;
                }))
                {
                    globalEnvValues[key] = value;
                }
                else
                {
                    newData[key] = value;
                }
            }

            if (newData.Count == 0)
            {
                resourceValues.Remove("env");
            }
            else
            {
                resourceValues["env"] = newData;
            }
        }

        values["global"] = new Dictionary<object, object>
        {
            ["env"] = globalEnvValues
        };

        var serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        var valuesFile = $"{chartPath}/values.yaml";
        var valuesYaml = serializer.Serialize(values);
        await File.AppendAllTextAsync(valuesFile, valuesYaml);
    }

    private static string FormatKebabToCamel(string value)
    {
        Span<char> newString = stackalloc char[value.Length];
        var newIndex = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '-')
            {
                newString[newIndex++] = value[i];
                continue;
            }

            if (i == value.Length - 1)
            {
                break;
            }

            newString[newIndex++] = !char.IsLower(value[++i]) ? value[i] : char.ToUpperInvariant(value[i]);
        }

        return newString[..newIndex].ToString();
    }
}
