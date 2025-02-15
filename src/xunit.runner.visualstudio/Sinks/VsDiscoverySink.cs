using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Adapter;
using Xunit.Abstractions;

#if NETCOREAPP
using System.Reflection;
#endif

namespace Xunit.Runner.VisualStudio;

public sealed class VsDiscoverySink : IMessageSinkWithTypes, IVsDiscoverySink, IDisposable
{
	static readonly string Ellipsis = new((char)183, 3);
	const int MaximumDisplayNameLength = 447;
	const int TestCaseDescriptorBatchSize = 100;

	static readonly Action<TestCase, string, string>? addTraitThunk = GetAddTraitThunk();
	static readonly Uri uri = new(Constants.ExecutorUri);

	readonly Func<bool> cancelThunk;
	readonly ITestCaseDescriptorProvider descriptorProvider;
	readonly ITestFrameworkDiscoveryOptions discoveryOptions;
	readonly ITestCaseDiscoverySink discoverySink;
	readonly DiscoveryEventSink discoveryEventSink = new();
	readonly LoggerHelper logger;
	readonly string source;
	readonly List<ITestCase> testCaseBatch = new();
	readonly TestPlatformContext testPlatformContext;
	readonly TestCaseFilter testCaseFilter;

	public VsDiscoverySink(
		string source,
		ITestFrameworkDiscoverer discoverer,
		LoggerHelper logger,
		ITestCaseDiscoverySink discoverySink,
		ITestFrameworkDiscoveryOptions discoveryOptions,
		TestPlatformContext testPlatformContext,
		TestCaseFilter testCaseFilter,
		Func<bool> cancelThunk)
	{
		this.source = source;
		this.logger = logger;
		this.discoverySink = discoverySink;
		this.discoveryOptions = discoveryOptions;
		this.testPlatformContext = testPlatformContext;
		this.testCaseFilter = testCaseFilter;
		this.cancelThunk = cancelThunk;

		descriptorProvider = (discoverer as ITestCaseDescriptorProvider) ?? new DefaultTestCaseDescriptorProvider(discoverer);

		discoveryEventSink.TestCaseDiscoveryMessageEvent += HandleTestCaseDiscoveryMessage;
		discoveryEventSink.DiscoveryCompleteMessageEvent += HandleDiscoveryCompleteMessage;
	}

	public ManualResetEvent Finished { get; } = new ManualResetEvent(initialState: false);

	public int TotalTests { get; private set; }

	public void Dispose()
	{
		Finished.Dispose();
		discoveryEventSink.Dispose();
	}

	public static TestCase? CreateVsTestCase(
		string source,
		TestCaseDescriptor descriptor,
		LoggerHelper logger,
		TestPlatformContext testPlatformContext)
	{
		try
		{
			var fqTestMethodName = $"{descriptor.ClassName}.{descriptor.MethodName}";
			var result = new TestCase(fqTestMethodName, uri, source) { DisplayName = Escape(descriptor.DisplayName) };

			if (testPlatformContext.RequireSerialization)
				result.SetPropertyValue(VsTestRunner.SerializedTestCaseProperty, descriptor.Serialization);

			result.Id = GuidFromString(uri + descriptor.UniqueID);
			result.CodeFilePath = descriptor.SourceFileName;
			result.LineNumber = descriptor.SourceLineNumber.GetValueOrDefault();

			if (addTraitThunk != null)
			{
				var traits = descriptor.Traits;

				foreach (var key in traits.Keys)
					foreach (var value in traits[key])
						addTraitThunk(result, key, value);
			}

			return result;
		}
		catch (Exception ex)
		{
			logger.LogErrorWithSource(source, "Error creating Visual Studio test case for {0}: {1}", descriptor.DisplayName, ex);
			return null;
		}
	}

	static string Escape(string value)
	{
		if (value == null)
			return string.Empty;

		return Truncate(value.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t"));
	}

	static string Truncate(string value)
	{
		if (value.Length <= MaximumDisplayNameLength)
			return value;

		return value.Substring(0, MaximumDisplayNameLength - Ellipsis.Length) + Ellipsis;
	}

	public int Finish()
	{
		Finished.WaitOne();
		return TotalTests;
	}

	static Action<TestCase, string, string>? GetAddTraitThunk()
	{
		try
		{
			var testCaseType = typeof(TestCase);
			var stringType = typeof(string);

#if NETCOREAPP
			var property = testCaseType.GetRuntimeProperty("Traits");
#else
			var property = testCaseType.GetProperty("Traits");
#endif
			if (property == null)
				return null;

#if NETCOREAPP
			var method = property.PropertyType.GetRuntimeMethod("Add", new[] { typeof(string), typeof(string) });
#else
			var method = property.PropertyType.GetMethod("Add", new[] { typeof(string), typeof(string) });
#endif
			if (method == null)
				return null;

			var thisParam = Expression.Parameter(testCaseType, "this");
			var nameParam = Expression.Parameter(stringType, "name");
			var valueParam = Expression.Parameter(stringType, "value");
			var instance = Expression.Property(thisParam, property);
			var body = Expression.Call(instance, method, new[] { nameParam, valueParam });

			return Expression.Lambda<Action<TestCase, string, string>>(body, thisParam, nameParam, valueParam).Compile();
		}
		catch (Exception)
		{
			return null;
		}
	}

	void HandleCancellation(MessageHandlerArgs args)
	{
		if (cancelThunk())
			args.Stop();
	}

	void HandleTestCaseDiscoveryMessage(MessageHandlerArgs<ITestCaseDiscoveryMessage> args)
	{
		testCaseBatch.Add(args.Message.TestCase);
		TotalTests++;

		if (testCaseBatch.Count == TestCaseDescriptorBatchSize)
			SendExistingTestCases();

		HandleCancellation(args);
	}

	void HandleDiscoveryCompleteMessage(MessageHandlerArgs<IDiscoveryCompleteMessage> args)
	{
		try
		{
			SendExistingTestCases();
		}
		finally
		{
			// Set test discovery complete despite any potential issues
			// with sending test cases over. This would avoid causing test discovery to hang for the entire session.
			Finished.Set();
		}

		HandleCancellation(args);
	}

	bool IMessageSinkWithTypes.OnMessageWithTypes(
		IMessageSinkMessage message,
		HashSet<string> messageTypes) =>
			discoveryEventSink.OnMessageWithTypes(message, messageTypes);

	private void SendExistingTestCases()
	{
		if (testCaseBatch.Count == 0)
			return;

		var descriptors = descriptorProvider.GetTestCaseDescriptors(testCaseBatch, includeSerialization: testPlatformContext.RequireSerialization);
		foreach (var descriptor in descriptors)
		{
			var vsTestCase = CreateVsTestCase(source, descriptor, logger, testPlatformContext);
			if (vsTestCase != null && testCaseFilter.MatchTestCase(vsTestCase))
			{
				if (discoveryOptions.GetInternalDiagnosticMessagesOrDefault())
					logger.LogWithSource(source, "Discovered test case '{0}' (ID = '{1}', VS FQN = '{2}')", descriptor.DisplayName, descriptor.UniqueID, vsTestCase.FullyQualifiedName);

				discoverySink.SendTestCase(vsTestCase);
			}
		}

		testCaseBatch.Clear();
	}

	readonly static HashAlgorithm Hasher = SHA1.Create();

	static Guid GuidFromString(string data)
	{
		var hash = Hasher.ComputeHash(Encoding.Unicode.GetBytes(data));
		var b = new byte[16];
		Array.Copy(hash, b, 16);
		return new Guid(b);
	}
}
