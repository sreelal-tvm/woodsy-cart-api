using WoodsyCart.Domain;

namespace WoodsyCart.Infrastructure.Contracts;

public record CartLineRequest(int ProductId, int? VariantId, int Quantity);

public record QuoteRequest(List<CartLineRequest> Items, string? CouponCode, string? Pincode, PaymentMethod PaymentMethod = PaymentMethod.Razorpay);

public record QuoteLineDto(
    int ProductId, int? VariantId, string Name, string? VariantName, string Slug, string? ImageUrl,
    int Quantity, decimal UnitMrp, decimal UnitPrice, decimal LineTotal, bool IsAvailable, string? Problem,
    decimal RegularPrice = 0, string? OfferName = null);

public record QuoteDto(
    List<QuoteLineDto> Lines, decimal SubTotal, decimal MrpTotal, decimal Discount, string? CouponCode, string? CouponMessage,
    decimal ShippingFee, decimal CodFee, decimal Total, decimal TaxAmount, bool CanCheckout, bool CodAvailable,
    int DeliveryDays, List<string> Messages, decimal OfferSavings = 0);

public record AddressDto(int Id, string Label, string FullName, string Phone, string Line1, string? Line2, string? Landmark,
    string City, string State, string Pincode, bool IsDefault, double? Latitude = null, double? Longitude = null);

public record AddressRequest(string Label, string FullName, string Phone, string Line1, string? Line2, string? Landmark,
    string City, string State, string Pincode, bool IsDefault, double? Latitude = null, double? Longitude = null);

public record PlaceOrderRequest(
    List<CartLineRequest> Items, int? AddressId, AddressRequest? Address, string? CouponCode,
    PaymentMethod PaymentMethod, string? Notes, string? CustomerGstin, string? GuestEmail = null);

public record PlaceOrderResponse(
    string OrderNumber, decimal Total, PaymentMethod PaymentMethod,
    string? RazorpayOrderId, string? RazorpayKeyId, long AmountInPaise,
    string CustomerName, string? CustomerEmail, string CustomerPhone, string AccessToken, bool IsGuest,
    string? PaymentProvider = null, string? GatewayMethod = null);

/// <summary>How to pay a pending direct-UPI or bank-transfer order (shown on the order page).</summary>
public record PaymentInstructionsDto(
    string Provider, decimal Amount, string? UpiId, string? PayeeName, string? UpiUri,
    string? AccountName, string? AccountNumber, string? Ifsc, string? BankName,
    string? CustomerUtr, bool AwaitingVerification);

public record SubmitUtrRequest(string Utr);

public record VerifyPaymentRequest(string OrderNumber, string RazorpayOrderId, string RazorpayPaymentId, string RazorpaySignature, string? AccessToken = null);

public record OrderItemDto(int Id, int ProductId, int? VariantId, string ProductName, string? VariantName, string? ImageUrl,
    string? Slug, int Quantity, decimal UnitMrp, decimal UnitPrice, decimal LineTotal, decimal GstRate, string? HsnCode,
    decimal RegularPrice = 0, string? OfferName = null);

public record OrderHistoryDto(OrderStatus Status, string? Note, DateTime CreatedAt);

public record OrderSummaryDto(int Id, string OrderNumber, DateTime CreatedAt, OrderStatus Status, PaymentStatus PaymentStatus,
    PaymentMethod PaymentMethod, decimal Total, int ItemCount, string? FirstItemImage, string? FirstItemName,
    DateTime? ExpectedDeliveryDate = null, DateTime? DeliveryDateChangedAt = null);

public record OrderDetailDto(
    int Id, string OrderNumber, DateTime CreatedAt, OrderStatus Status, PaymentStatus PaymentStatus, PaymentMethod PaymentMethod,
    string ShipName, string ShipPhone, string? ShipEmail, string ShipLine1, string? ShipLine2, string? ShipLandmark,
    string ShipCity, string ShipState, string ShipPincode,
    decimal SubTotal, decimal Discount, string? CouponCode, decimal ShippingFee, decimal CodFee, decimal Total, decimal TaxAmount,
    decimal AmountPaid, string? CustomerNotes, DateTime? ExpectedDeliveryDate, DateTime? DeliveredAt,
    string? TrackingNumber, string? CourierName, string? CancelReason,
    List<OrderItemDto> Items, List<OrderHistoryDto> History, bool CanCancel, bool CanReturn, bool HasInvoice, string? InvoiceNumber,
    string? PaymentProvider = null, PaymentInstructionsDto? Payment = null,
    DateTime? OriginalDeliveryDate = null, string? DeliveryDateReason = null, DateTime? DeliveryDateChangedAt = null,
    bool CanReview = false, List<OrderReviewDto>? Reviews = null);

/// <summary>The customer's own review of a product in this order (shown on the order page, approved or not).</summary>
public record OrderReviewDto(int ProductId, int Rating, string? Title, string? Comment, bool IsApproved, DateTime WrittenAt);

/// <summary>Rate a delivered item from the order page. Writing again edits the earlier review.</summary>
public record OrderReviewRequest(int ProductId, int Rating, string? Title, string? Comment);

public record CancelOrderRequest(string Reason);

public record ReturnRequest(string Reason, string? Comment);

public record CouponValidateRequest(string Code, decimal SubTotal);
