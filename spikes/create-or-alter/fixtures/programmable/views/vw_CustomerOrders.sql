CREATE VIEW dbo.vw_CustomerOrders
AS
    SELECT c.Id                    AS CustomerId,
           c.Name                  AS CustomerName,
           COUNT(o.Id)             AS OrderCount,
           dbo.fn_OrderTotal(c.Id) AS LifetimeTotal
    FROM dbo.Customers AS c
    LEFT JOIN dbo.Orders AS o ON o.CustomerId = c.Id
    GROUP BY c.Id, c.Name;
GO
