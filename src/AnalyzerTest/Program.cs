using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateDefaultBuilder(args);

// Add DbContextFactory to the DI container
builder.ConfigureServices((hostContext, services) =>
{
	services.AddDbContext<MyDbContext>(options =>
	   options.UseInMemoryDatabase("TestDatabase")); // Only configure it once in DI setup
	services.AddDbContextFactory<MyDbContext>(); // Register the DbContext factory
});

var app = builder.Build();


var scope = app.Services.CreateScope();
var contextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MyDbContext>>();
var options = new DbContextOptionsBuilder<MyDbContext>()
		   .UseInMemoryDatabase("TestDatabase")
		   .Options;

// UNSAFE: Parallel execution with the same DbContext instance
var dbContext1 = scope.ServiceProvider.GetRequiredService<MyDbContext>();
var dbContext2 = scope.ServiceProvider.GetRequiredService<MyDbContext>();

//USAFE parallel execution on same dbcontext
var result = dbContext1.Products
	.AsParallel()  // Warning
	.Where(x => x.Name == "value").ToList();

//USAFE parallel execution on dbcontext
Parallel.ForEach(result, item =>
{
	dbContext1.Products.Add(item); // Warning: DbContext usage in Parallel.ForEach
	dbContext1.SaveChanges();      // Warning: DbContext usage in Parallel.ForEach
});

//UNSAFE parallel execution on dbcontext
await Parallel.ForEachAsync(result, async (item, cancellationToken) =>
{
	dbContext1.Products.Add(item); // Warning: DbContext usage in Parallel.ForEach
	await dbContext1.SaveChangesAsync();      // Warning: DbContext usage in Parallel.ForEach
});
//Safe approach
result = dbContext1.Products.ToList();
result.AsParallel()  
	.Where(x => x.Name == "value").ToList();

// UNSAFE: Accessing shared DbContext instance across threads 
Task.WhenAll(
	Task.Run(() => dbContext1.SaveChanges()),  // Warning
	Task.Run(() => dbContext2.SaveChanges())   // Warning
).Wait();

// UNSAFE: Multiple tasks accessing the same DbContext instance
var t1 = Task.Run(() => dbContext1.SaveChanges()); // Warning
var t2 = Task.Run(() => dbContext2.SaveChanges()); // Warning
await Task.WhenAll(t1, t2);

// UNSAFE: not in correct ussing scope
using (var dbContext3 = new MyDbContext(options))
{
	var t3 = Task.Run(() => dbContext3.SaveChanges()); // Warning

}
await Task.Factory.StartNew(() =>
{
	using (var dbContext5 = new MyDbContext(options))
	{
		dbContext5.SaveChanges(); // Warning
	}
});

// SAFE: Using separate DbContext instances for parallel tasks
await Task.WhenAll(
	Task.Run(async () =>
	{
		using (var dbContext1 = new MyDbContext(options))
		{
			await dbContext1.SaveChangesAsync();
		}
	}),
	Task.Run(() =>
	{
		using (var dbContext2 = new MyDbContext(options))
		{
			dbContext2.SaveChanges();
		}
	})
);

// SAFE: DbContext used within a single thread
UpdateData(dbContext1);
void UpdateData(MyDbContext dbContext)
{
	dbContext.SaveChanges();
}

// SAFE: Using DbContextFactory to create and dispose contexts safely
var safeTask = Task.Run(async () =>
{
	using var context = contextFactory.CreateDbContext();
	await context.SaveChangesAsync();
});

// UNSAFE: Mixing DbContext instances incorrectly
var unsafeTask = Task.Run(async () =>
{
	using var context1 = contextFactory.CreateDbContext();
	await dbContext1.SaveChangesAsync(); // Warning: Mixing context instances
});

// SAFE: Isolating DbContext instances in tasks
var safeTask2 = Task.Run(async () =>
{
	using (var context = contextFactory.CreateDbContext())
	{
		await context.SaveChangesAsync();
	}
});

// UNSAFE
using var context5 = contextFactory.CreateDbContext();
var task7 = Task.Run(() => context5.SaveChanges()); // Warning
var task8 = Task.Run(() => dbContext1.SaveChanges()); // Warning
await Task.WhenAll(task7, task8);

// UNSAFE: Accessing shared DbContext instance across threads (currently not supported)
Thread thread = new(() =>
{
	var result = dbContext1.Products.ToList(); // Warning: DbContext usage inside thread
});

// Creating and adding some sample data
var product1 = new Product { Id = 20, Name = "Product1", Price = 10.0 };
var product2 = new Product { Id = 21, Name = "Product2", Price = 15.0 };
dbContext1.Products.AddRange(product1, product2);
await dbContext1.SaveChangesAsync();




public class MyDbContext : DbContext
{
	public DbSet<Product> Products { get; set; }

	public MyDbContext(DbContextOptions<MyDbContext> options) : base(options)
	{
	}
}

public class Product
{
	public int Id { get; set; }
	public string? Name { get; set; }
	public double Price { get; set; }
}
