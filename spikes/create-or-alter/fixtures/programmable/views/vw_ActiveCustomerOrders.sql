-- Depends on vw_CustomerOrders. Alphabetically this file sorts FIRST,
-- so naive file-order application must fail here — proving dependency
-- ordering is load-bearing.
CREATE VIEW dbo.vw_ActiveCustomerOrders
AS
    SELECT CustomerId, CustomerName, OrderCount, LifetimeTotal
    FROM dbo.vw_CustomerOrders
    WHERE OrderCount > 0;
GO
