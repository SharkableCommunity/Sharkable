using System.Text;
using FluentValidation;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using Sharkable;
using Sharkable.NativeTest;
using SqlSugar;

var builder = WebApplication.CreateSlimBuilder(args);

// ── Services ──────────────────────────────────────────────
builder.Services.AddSingleton<ShoppingDbContext>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<OrderSagaSteps>();
builder.Services.AddSingleton<IValidator<CreateProductRequest>, CreateProductValidator>();

// ── JWT config ────────────────────────────────────────────
var jwtKey = builder.Configuration["Jwt:Key"] ?? "DefaultSampleKey12345678901234567890";
var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "sharkable-shop";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "sharkable-shop-api";

// ── JSON serialization (AOT-safe) ─────────────────────────
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, ShoppingJsonContext.Default);
});

// ── Sharkable ─────────────────────────────────────────────
builder.Services.AddShark([typeof(Program).Assembly], opt =>
{
    opt.Format = EndpointFormat.SnakeCase;
    opt.EnableValidation = true;
    opt.EnableHealthChecks = true;
    opt.EnableIdempotency = true;
    opt.EnableAutoWrap = true;
    opt.ConfigureTracing(t => t.ServiceName = "sharkable-shop");

    // AutoCrud test — SQLite entity (absolute path)
    // Use AppContext.BaseDirectory: Assembly.Location is empty in single-file/AOT apps (IL3000).
    var dbPath = Path.Combine(AppContext.BaseDirectory, "test.db");
    opt.ConfigureAutoCrud(s =>
    {
        s.DbType = Sharkable.DbType.Sqlite;
        s.ConnectionString = $"DataSource={dbPath}";
    });

    // JWT — self-issued, no OIDC authority
    opt.ConfigureJwt(jwt =>
    {
        jwt.Authority = jwtIssuer;
        jwt.Audiences = [jwtAudience];
        jwt.BearerConfigure = jwtBearer =>
        {
            jwtBearer.Authority = null;
            jwtBearer.ConfigurationManager = null;
            jwtBearer.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = jwtIssuer,
                ValidateAudience = true,
                ValidAudience = jwtAudience,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                RequireSignedTokens = true,
                RequireExpirationTime = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
                NameClaimType = JwtRegisteredClaimNames.Sub,
            };
        };
    });

    opt.ConfigureGracefulShutdown(g => g.DrainTimeout = TimeSpan.FromSeconds(15));

    opt.ConfigureAuditTrail(a =>
    {
        a.AsyncWrite = true;
        a.BatchSize = 50;
    });

    opt.ConfigureRateLimiting(r =>
    {
        r.DefaultLimit = 200;
        r.DefaultWindow = TimeSpan.FromMinutes(1);
        r.IncludeHeaders = true;
        r.HeaderPrefix = "X-RateLimit";
    });

    opt.RateLimiterConfigure = rateLimiterOptions =>
    {
        rateLimiterOptions.AddFixedWindowLimiter("auth", o =>
        {
            o.PermitLimit = 10;
            o.Window = TimeSpan.FromMinutes(1);
            o.QueueLimit = 0;
        });
        rateLimiterOptions.AddFixedWindowLimiter("cart", o =>
        {
            o.PermitLimit = 60;
            o.Window = TimeSpan.FromMinutes(1);
            o.QueueLimit = 0;
        });
        rateLimiterOptions.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    };

    opt.ConfigureCronJobs = async scheduler =>
    {
        await scheduler.RegisterAsync(new CronJob(
            "cleanup-expired-carts",
            "0 */5 * * * ?",
            async ct =>
            {
                var logger = Shark.GetService<ILogger<Program>>();
                logger?.LogInformation("Cron: cleaning up expired carts...");
            },
            new CronJobOptions { RetryCount = 1, Timeout = TimeSpan.FromSeconds(30) }));
    };

    // AutoCrud: init tables at startup
    opt.ConfigureOnStarted(sp =>
    {
        var sqlSugar = sp.GetRequiredService<ISqlSugarClient>();
        sqlSugar.CodeFirst.InitTables(typeof(TestItem));
    });
});

// ── Build ─────────────────────────────────────────────────
var app = builder.Build();

// ── Seed data ─────────────────────────────────────────────
SeedData(app.Services.GetRequiredService<ShoppingDbContext>(),
    app.Services.GetRequiredService<AuthService>());

// ── Sharkable pipeline ────────────────────────────────────
app.UseShark(opt =>
{
    opt.EnableAutoWrap = true;
});

// ── Run ───────────────────────────────────────────────────
app.Run();

// ── Seed method ───────────────────────────────────────────
static void SeedData(ShoppingDbContext db, AuthService auth)
{
    // Admin user created via registration
    var (_, _) = auth.Register(new RegisterRequest("admin", "admin@shop.com", "admin123", "Admin"));
    // Customer user
    var (_, _) = auth.Register(new RegisterRequest("alice", "alice@shop.com", "alice123", "Alice"));

    var seedProducts = new[]
    {
        new CreateProductRequest("Wireless Headphones", "Noise-cancelling Bluetooth headphones with 30hr battery", 79.99m, 50, "Electronics", "/images/headphones.jpg"),
        new CreateProductRequest("Running Shoes", "Lightweight mesh running shoes, breathable design", 129.99m, 30, "Clothing", "/images/shoes.jpg"),
        new CreateProductRequest("Organic Coffee Beans", "Fair-trade single origin, medium roast 500g", 24.99m, 100, "Food", "/images/coffee.jpg"),
        new CreateProductRequest("Mechanical Keyboard", "Cherry MX switches, RGB backlit, 87-key", 149.99m, 25, "Electronics", "/images/keyboard.jpg"),
        new CreateProductRequest("Yoga Mat", "Non-slip TPE material, 6mm thickness, eco-friendly", 39.99m, 60, "Sports", "/images/yogamat.jpg"),
        new CreateProductRequest("Cotton T-Shirt", "Premium organic cotton, unisex fit, crew neck", 29.99m, 80, "Clothing", "/images/tshirt.jpg"),
        new CreateProductRequest("USB-C Hub", "7-in-1 multiport adapter with 4K HDMI, SD card reader", 45.99m, 40, "Electronics", "/images/usbhub.jpg"),
        new CreateProductRequest("Dark Chocolate Bar", "72% cacao, organic, single-origin Madagascar", 8.99m, 200, "Food", "/images/chocolate.jpg"),
    };

    foreach (var p in seedProducts)
    {
        db.AddProduct(new Product(
            db.NextProductId(), p.Name, p.Description, p.Price, p.Stock,
            p.Category, p.ImageUrl, DateTime.UtcNow));
    }
}

// ── FluentValidation ─────────────────────────────────────
public sealed class CreateProductValidator : AbstractValidator<CreateProductRequest>
{
    public CreateProductValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).NotEmpty().MaximumLength(2000);
        RuleFor(x => x.Price).GreaterThan(0);
        RuleFor(x => x.Stock).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Category).NotEmpty().MaximumLength(100);
    }
}
