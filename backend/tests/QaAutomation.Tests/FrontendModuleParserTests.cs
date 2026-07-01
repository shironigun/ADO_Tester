using QaAutomation.Core.Agents;

namespace QaAutomation.Tests;

/// <summary>Unit tests for FrontendModuleParser — the strict source of the QA Loop module dropdown.
/// Fixture mirrors the real web-app/.../routes/Routes.tsx shape (default + named imports, flat route
/// objects, param sub-routes, an interface with path:/component: that must NOT match).</summary>
public sealed class FrontendModuleParserTests
{
    private const string Root = "web-app/src/target-crm/src";
    private const string BaseUrl = "https://targetcrmqa.z19.web.core.windows.net";

    private const string Routes = """
        import CustomersPage from 'components/customer/CustomersPage';
        import LeadsPage from 'components/leads';
        import Deals from 'components/deals/deals';
        import TaskBoard from 'components/lead/components/task-board/task-board';
        import { SurveyPage, SurveysPage } from 'components/survey';
        import PrivateRoute from './private-route';
        import config from 'config';

        interface RouteType { exact?: boolean; path: string; component: any; }

        const privateRoutes: RouteType[] = [
          { path: '/tasks', component: TaskBoard, exact: true },
          { path: '/customers', component: CustomersPage, exact: true },
          { path: '/leads', component: LeadsPage, exact: true },
          { path: '/', exact: true, component: DashboardPage },
          { path: '/surveys', component: SurveysPage, exact: true },
          { path: '/surveys/:id/designer', component: SurveyPage, exact: true },
          { path: '/deals', component: Deals, exact: true },
        ];
        """;

    [Fact]
    public void Parse_extracts_modules_with_derived_name_component_path_and_url()
    {
        var mods = FrontendModuleParser.Parse(Routes, Root, BaseUrl);

        var deals = Assert.Single(mods, m => m.ModuleName == "deals");
        Assert.Equal("/deals", deals.RoutePath);
        Assert.Equal("web-app/src/target-crm/src/components/deals", deals.ComponentPath);
        Assert.Equal("https://targetcrmqa.z19.web.core.windows.net/deals", deals.Url);
    }

    [Fact]
    public void Parse_derives_module_from_first_segment_after_components_for_nested_imports()
    {
        var mods = FrontendModuleParser.Parse(Routes, Root, BaseUrl);
        // TaskBoard imports components/lead/components/task-board/task-board → module "lead", route /tasks
        var lead = Assert.Single(mods, m => m.ModuleName == "lead");
        Assert.Equal("/tasks", lead.RoutePath);
        Assert.Equal("web-app/src/target-crm/src/components/lead", lead.ComponentPath);
    }

    [Fact]
    public void Parse_skips_root_param_routes_and_the_interface_declaration()
    {
        var mods = FrontendModuleParser.Parse(Routes, Root, BaseUrl);
        Assert.DoesNotContain(mods, m => m.RoutePath == "/");                       // root skipped
        Assert.DoesNotContain(mods, m => m.RoutePath.Contains(':'));                // :id sub-route skipped
        Assert.DoesNotContain(mods, m => m.ModuleName == "any");                    // interface not matched
        Assert.Contains(mods, m => m.ModuleName == "survey" && m.RoutePath == "/surveys"); // named import resolved
    }

    [Fact]
    public void Parse_dedupes_by_module_and_handles_empty_input()
    {
        Assert.Empty(FrontendModuleParser.Parse("", Root, BaseUrl));
        var mods = FrontendModuleParser.Parse(Routes, Root, BaseUrl);
        Assert.Equal(mods.Select(m => m.ModuleName).Distinct().Count(), mods.Count);
    }
}
