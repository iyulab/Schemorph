CREATE PROCEDURE dbo.GetTopCustomers
    @Top INT = 10
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP (@Top) CustomerId, CustomerName, LifetimeTotal
    FROM dbo.vw_CustomerOrders
    ORDER BY LifetimeTotal DESC;
END
GO
