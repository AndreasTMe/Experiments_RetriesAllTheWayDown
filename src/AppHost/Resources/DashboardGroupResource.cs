namespace AppHost.Resources;

// A resource with no process: it only gives other resources a parent to be grouped under in the dashboard.
internal sealed class DashboardGroupResource(string name) : Resource(name);