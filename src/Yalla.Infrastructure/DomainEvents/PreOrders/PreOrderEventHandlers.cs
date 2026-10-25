using Microsoft.Extensions.DependencyInjection;
using Yalla.Application.Abstractions;
using Yalla.Domain.Common;
using Yalla.Domain.Occupancy;

namespace Yalla.Infrastructure.DomainEvents.PreOrders;

/// <summary>
/// The pre-order vertical's handlers (<c>docs/domain-events.md</c>, pre-orders), registered from
/// <c>DependencyInjection.AddDomainEventHandlers</c>. An event's handlers run in the order they are listed here: the
/// History's line, the question moved or withdrawn, the tab's stream, then signal 6 - the order the services wrote them.
/// </summary>
internal static class PreOrderEventHandlers
{
    public static void Add(IServiceCollection services)
    {
        services.AddScoped<PreOrderActivity>();
        services.AddScoped<OnYourWayQuestion>();
        services.AddScoped<PreOrderArrivalStream>();
        services.AddScoped<PreOrderRailSignal>();

        Handle<PreOrderSaved, OnYourWayQuestion>(services);
        Handle<PreOrderSaved, PreOrderRailSignal>(services);
        Handle<PreOrderCleared, OnYourWayQuestion>(services);
        Handle<PreOrderCleared, PreOrderRailSignal>(services);
        Handle<PreOrderFollowedMove, PreOrderRailSignal>(services);
        Handle<PreOrderSent, PreOrderActivity>(services);
        Handle<PreOrderSent, OnYourWayQuestion>(services);
        Handle<PreOrderSent, PreOrderRailSignal>(services);
        Handle<PartyMarkedNotComing, PreOrderActivity>(services);
        Handle<PartyMarkedNotComing, OnYourWayQuestion>(services);
        Handle<PartyMarkedNotComing, PreOrderRailSignal>(services);
        Handle<HeldTicketMoved, PreOrderRailSignal>(services);
        Handle<HeldTicketVoided, PreOrderRailSignal>(services);
        Handle<PreOrderArrivedOnTab, PreOrderArrivalStream>(services);
        Handle<PreOrderArrivedOnTab, PreOrderRailSignal>(services);
        Handle<PreOrderSettingsSaved, PreOrderRailSignal>(services);
        Handle<GuestOnTheirWay, PreOrderActivity>(services);
        Handle<GuestOnTheirWay, OnYourWayQuestion>(services);
        Handle<GuestOnTheirWay, PreOrderRailSignal>(services);
        Handle<GuestRunningLate, PreOrderActivity>(services);
        Handle<GuestRunningLate, OnYourWayQuestion>(services);
        Handle<GuestRunningLate, PreOrderRailSignal>(services);
        Handle<OnMyWayAskRecorded, PreOrderRailSignal>(services);
        Handle<OnMyWayAnswersForgotten, PreOrderRailSignal>(services);
    }

    /// <summary>One event to one handler, which handles several events of the vertical as one scoped instance.</summary>
    private static void Handle<TEvent, THandler>(IServiceCollection services)
        where TEvent : IDomainEvent
        where THandler : class, IDomainEventHandler<TEvent> =>
        services.AddScoped<IDomainEventHandler<TEvent>>(sp => sp.GetRequiredService<THandler>());
}
