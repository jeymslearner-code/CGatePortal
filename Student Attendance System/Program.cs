var builder = WebApplication.CreateBuilder(args);

// Add MVC services to the container
builder.Services.AddControllersWithViews();

// CONFIGURATION: Enable Session storage to keep track of the logged-in Instructor and their Subject
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(60); // Session lasts for 1 hour (standard class duration)
    options.Cookie.HttpOnly = true; // Security: Prevents client-side scripts from accessing the session cookie
    options.Cookie.IsEssential = true; // Ensures the session works even if the user hasn't accepted non-essential cookies
});

var app = builder.Build();

// Configure the HTTP request pipeline
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles(); // Allows the app to load CSS, Images, and JavaScript files
app.UseRouting();

// MIDDLEWARE: UseSession must be placed after UseRouting and before UseAuthorization
// This allows the system to identify who is logged in before checking their permissions
app.UseSession();
app.UseAuthorization();

// ROUTING: Sets the Login page as the first thing the user sees when opening the portal
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Login}/{id?}");

app.Run();