CREATE PROCEDURE dbo.GetCustomerSummary
    @CustomerId INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT CustomerId, CustomerName, OrderCount, LifetimeTotal
    FROM dbo.vw_ActiveCustomerOrders
    WHERE CustomerId = @CustomerId;
END
GO
