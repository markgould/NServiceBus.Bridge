using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NServiceBus;
using NServiceBus.Bridge;
using NServiceBus.Extensibility;
using NServiceBus.Raw;
using NServiceBus.Routing;
using NServiceBus.Transport;
using NServiceBus.Unicast.Subscriptions;
using NServiceBus.Unicast.Subscriptions.MessageDrivenSubscriptions;

class PublishRouter : IRouter
{
    ISubscriptionStorage subscriptionStorage;
    IDistributionPolicy distributionPolicy;

    public PublishRouter(ISubscriptionStorage subscriptionStorage, IDistributionPolicy distributionPolicy)
    {
        this.subscriptionStorage = subscriptionStorage;
        this.distributionPolicy = distributionPolicy;
    }

    public async Task Route(MessageContext context, MessageIntentEnum intent, IRawEndpoint dispatcher)
    {
        string messageTypes;
        if (!context.Headers.TryGetValue(Headers.EnclosedMessageTypes, out messageTypes))
        {
            throw new UnforwardableMessageException("Message need to have 'NServiceBus.EnclosedMessageTypes' header in order to be routed.");
        }
        var types = messageTypes.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        var typeObjects = types.Select(t => new MessageType(t));

        var subscribers = await subscriptionStorage.GetSubscriberAddressesForMessage(typeObjects, new ContextBag()).ConfigureAwait(false);

        var destinations = SelectDestinationsForEachEndpoint(subscribers);
        var outgoingMessage = new OutgoingMessage(context.MessageId, context.Headers, context.Body);
        var operations = destinations.Select(x => new TransportOperation(outgoingMessage, new UnicastAddressTag(x)));

        await dispatcher.Dispatch(new TransportOperations(operations.ToArray()), context.TransportTransaction, context.Extensions).ConfigureAwait(false);
    }

    IEnumerable<string> SelectDestinationsForEachEndpoint(IEnumerable<Subscriber> subscribers)
    {
        //Make sure we are sending only one to each transport destination. Might happen when there are multiple routing information sources.
        var addresses = new HashSet<string>();
        Dictionary<string, List<string>> groups = null;
        foreach (var subscriber in subscribers)
        {
            if (subscriber.Endpoint == null)
            {
                addresses.Add(subscriber.TransportAddress);
                continue;
            }

            groups = groups ?? new Dictionary<string, List<string>>();

            List<string> transportAddresses;
            if (groups.TryGetValue(subscriber.Endpoint, out transportAddresses))
            {
                transportAddresses.Add(subscriber.TransportAddress);
            }
            else
            {
                groups[subscriber.Endpoint] = new List<string> { subscriber.TransportAddress };
            }
        }

        if (groups != null)
        {
            foreach (var group in groups)
            {
                var instances = group.Value.ToArray(); // could we avoid this?
                var subscriber = distributionPolicy.GetDistributionStrategy(group.Key, DistributionStrategyScope.Publish).SelectReceiver(instances);
                addresses.Add(subscriber);
            }
        }

        return addresses;
    }
}
