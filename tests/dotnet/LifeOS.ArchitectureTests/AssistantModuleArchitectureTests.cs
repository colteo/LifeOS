using System.Reflection;
using LifeOS.Application.Assistant;
using LifeOS.Application.Nutrition;
using LifeOS.Application.Nutrition.Assistant;
using LifeOS.Application.Nutrition.Hydration;
using LifeOS.Application.Nutrition.Hydration.Assistant;
using LifeOS.Application.Persistence;
using NetArchTest.Rules;

namespace LifeOS.ArchitectureTests;

// AI-008: the water and meal modules follow Finance's rules. Their records are written only by the confirmation
// (the draft policy, inside the lifecycle's unit of work) through the modules' existing use cases; the modules
// the turn uses prepare drafts only, and can neither confirm, cancel, write nor ask for a nutrition estimate.
public class AssistantModuleArchitectureTests
{
    private static readonly Types Application = Types.InAssembly(Assembly.Load("LifeOS.Application"));

    [Theory]
    [InlineData("LifeOS.Application.Nutrition.Hydration.Assistant", typeof(AddHydrationIntakeHandler), typeof(HydrationDraftPolicy))]
    [InlineData("LifeOS.Application.Nutrition.Assistant", typeof(CreateMealHandler), typeof(MealDraftPolicy))]
    public void Only_The_Draft_Policy_Writes_The_Module_Record(string assistantNamespace, Type writeHandler, Type policy)
    {
        // Compiler-generated state machines count as their declaring type.
        var users = Application.That().ResideInNamespace(assistantNamespace).And().HaveDependencyOn(writeHandler.FullName!).GetTypes()
            .Select(type => type.FullName!.Split('+')[0]).Distinct().ToList();

        Assert.Equal([policy.FullName], users);
    }

    [Theory]
    [InlineData(typeof(HydrationAssistantModule))]
    [InlineData(typeof(MealAssistantModule))]
    public void The_Turn_Modules_Cannot_Confirm_Cancel_Or_Write(Type module)
    {
        var result = Application
            .That().HaveName(module.Name)
            .ShouldNot()
            .HaveDependencyOnAny(
                "LifeOS.Application.Assistant.AssistantOperationLifecycle`1",
                "LifeOS.Application.Assistant.IAssistantOperationRepository`1",
                "LifeOS.Application.Assistant.IAssistantDraftPolicy`1",
                typeof(AddHydrationIntakeHandler).FullName!,
                typeof(IHydrationRepository).FullName!,
                typeof(CreateMealHandler).FullName!,
                typeof(IMealEntryRepository).FullName!,
                typeof(INutritionEstimationService).FullName!,
                typeof(IUnitOfWork).FullName!)
            .GetResult();

        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    // The meal module records text only: no part of the assistant reaches the nutrition estimate (NUT-002).
    [Fact]
    public void No_Assistant_Part_Asks_For_A_Nutrition_Estimate()
    {
        var result = Application
            .That().ResideInNamespaceEndingWith(".Assistant")
            .ShouldNot()
            .HaveDependencyOnAny(typeof(INutritionEstimationService).FullName!, typeof(MealNutritionEstimation).FullName!,
                typeof(EstimateMealNutritionHandler).FullName!)
            .GetResult();

        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    // The core routes; it implements the one capability the turn processor sees.
    [Fact]
    public void The_Router_Is_The_Only_Capability()
    {
        var capabilities = Application.That().ImplementInterface(typeof(IAssistantCapability)).GetTypes().Select(type => type.Name).ToList();
        var modules = Application.That().ImplementInterface(typeof(IAssistantModule)).GetTypes().Select(type => type.Name).Order().ToList();

        Assert.Equal([nameof(AssistantCapabilityRouter)], capabilities);
        Assert.Equal(["FinanceAssistantModule", nameof(HydrationAssistantModule), nameof(MealAssistantModule)], modules);
    }
}
