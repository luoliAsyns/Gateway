
using LuoliCommon.DTO.ExternalOrder;
using LuoliCommon.Entities;
using System.Security.Cryptography;
using ThirdApis;

namespace GatewayService.Services.ExternalOrder
{

    public class ExternalOrderRepository : IExternalOrderRepository
    {

        private readonly AsynsApis _asynsApis;
        public ExternalOrderRepository(AsynsApis asynsApis)
        {
            _asynsApis = asynsApis;
        }
        public async Task<ApiResponse<bool>> Delete(ExternalOrderDTO dto)
        {
            var result = await _asynsApis.ExternalOrderDelete(new DeleteRequest()
            {
                from_platform = dto.FromPlatform,
                tid = dto.Tid,
            });

            return result;
        }

        public async Task<ApiResponse<ExternalOrderDTO>> Get(string from_platform, string tid)
        {
            var result = await _asynsApis.ExternalOrderQuery(from_platform, tid);
           
            return result;
        }

        public async Task<ApiResponse<ExternalOrderDTO>> Get(string coupon)
        {
            var result = await _asynsApis.CouponQuery(coupon);
            if (result.data is null)
                return new ApiResponse<ExternalOrderDTO>() { 
                    code= LuoliCommon.Enums.EResponseCode.Fail,
                    msg=$"cannot found coupon {coupon}",
                    data= null,
                };

            return await Get(result.data.ExternalOrderFromPlatform,
                result.data.ExternalOrderTid);
        }

        public async Task<ApiResponse<bool>> Insert(ExternalOrderDTO dto)
        {
            var result = await _asynsApis.ExternalOrderInsert(dto);
            return result;
        }

        public async Task<ApiResponse<bool>> Update(ExternalOrderDTO dto)
        {
            var result = await _asynsApis.ExternalOrderUpdate(dto);
            return result;
        }
    }
}
