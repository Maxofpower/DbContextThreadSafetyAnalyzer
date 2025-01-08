using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing.Verifiers;
using Xunit;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using System.Collections.Immutable;
using System.Reflection;

namespace DbContextThreadSafetyAnalyzer.Tests
{
	public class DbContextThreadSafetyAnalyzerTests
	{
		[Fact]
		public async Task DbContextUsageInTaskRun_ShouldTriggerDiagnostic()
		{
			var testCode = @"
            using System;
            using System.Linq;
            using System.Threading.Tasks;
            using Microsoft.EntityFrameworkCore;

            public class TestClass
            {
                public void TestMethod()
                {
                 var options = new DbContextOptionsBuilder<MyDbContext>()
                              .UseInMemoryDatabase(""TestDatabase"") // Correctly using the InMemoryDatabase extension
                               .Options;

                    var dbContext = new MyDbContext(options);

                    Task.Run(() => {
                        dbContext.SomeMethod(); // Accessing DbContext in a task
                    });
                }
            }

            public class MyDbContext : DbContext
            {
                public MyDbContext(DbContextOptions<MyDbContext> options) : base(options) { }
                public DbSet<MyEntity> MyEntities { get; set; }
                public void SomeMethod() => MyEntities.ToList(); // Simulate DbContext usage
            }

            public class MyEntity
            {
                public int Id { get; set; }
                public string Name { get; set; }
            }
        ";


			// The expected diagnostic location should be at the location where DbContext is accessed
			var expected = DiagnosticResult
	   .CompilerWarning(DbContextThreadSafetyAnalyzer.DiagnosticId)
	   .WithSpan(18, 25, 18, 34) // Adjust location based on your test code
	   .WithMessage("DbContext should not be shared across threads");

			await VerifyAnalyzerAsync(testCode, expected);
		}

		private static async Task VerifyAnalyzerAsync(string source, params DiagnosticResult[] expected)
		{
			var test = new CSharpAnalyzerTest<DbContextThreadSafetyAnalyzer, DefaultVerifier>
			{
				TestCode = source,
				ReferenceAssemblies = ReferenceAssemblies.Net.Net80
			};
			// Include required references to EntityFrameworkCore
			test.TestState.AdditionalReferences.Add(typeof(DbContextOptionsBuilder).Assembly);
			test.TestState.AdditionalReferences.Add(typeof(Microsoft.EntityFrameworkCore.InMemoryDbContextOptionsExtensions).Assembly);
			test.ExpectedDiagnostics.AddRange(expected);

			// Make sure we're not allowing compilation errors in the test code.
			// In some cases, it's better to mock dependencies rather than actually running them
			// for these kinds of tests. The analyzer should still detect issues related to DbContext usage
			// regardless of actual code compilation.

			// Adding the correct references and verifying diagnostics
			//var references = ImmutableArray.Create<Assembly>(e DbContext);
			//test.ReferenceAssemblies.AddAssemblies(references);
			await test.RunAsync();
		}
	}
}
