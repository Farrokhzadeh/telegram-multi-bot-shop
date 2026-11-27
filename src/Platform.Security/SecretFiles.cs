using System.Text.Json;

namespace Platform.Security;

public enum SecretService { Api, BotHost, JobHost }
public enum SecretConfigurationFailure { InvalidConfiguration, UnavailableFile, UnsafeFile, UnauthorizedScope, IdentityMismatch, ReusedKeyMaterial }

public sealed class SecretConfigurationException : Exception
{
	internal SecretConfigurationException(SecretConfigurationFailure failure) : base("Secret configuration rejected.") => Failure = failure;
	public SecretConfigurationFailure Failure { get; }
}

public static class SecretFiles
{
	internal static bool IsFileName(string value) => !string.IsNullOrEmpty(value) && value.Length <= 160 && char.IsAsciiLetterOrDigit(value[0]) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
	internal static Dictionary<string,JsonElement> Object(JsonElement element,params string[] names)
	{
		if(element.ValueKind!=JsonValueKind.Object) Invalid();
		var fields=new Dictionary<string,JsonElement>(StringComparer.Ordinal);
		foreach(var property in element.EnumerateObject()) if(!names.Contains(property.Name,StringComparer.Ordinal)||!fields.TryAdd(property.Name,property.Value)) Invalid();
		if(fields.Count!=names.Length) Invalid();
		return fields;
	}
	[System.Diagnostics.CodeAnalysis.DoesNotReturn] private static void Invalid()=>throw new SecretConfigurationException(SecretConfigurationFailure.InvalidConfiguration);
}
